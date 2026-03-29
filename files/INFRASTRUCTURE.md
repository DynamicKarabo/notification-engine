# Infrastructure — Real-Time Notification & Live Dashboard Engine

## Table of Contents

1. [Azure Infrastructure](#1-azure-infrastructure)
2. [Kubernetes Deployment](#2-kubernetes-deployment)
3. [Terraform IaC](#3-terraform-iac)
4. [CI/CD Pipeline](#4-cicd-pipeline)
5. [Environment Configuration](#5-environment-configuration)
6. [Scaling & Auto-Scaling](#6-scaling--auto-scaling)
7. [Networking & Security](#7-networking--security)
8. [Disaster Recovery](#8-disaster-recovery)

---

## 1. Azure Infrastructure

### Resource Topology

```
Azure Subscription
└── Resource Group: rg-notif-{env}
    │
    ├── Azure Kubernetes Service (AKS)
    │   └── Node Pool: System (3× Standard_D4s_v5)
    │   └── Node Pool: App (2–10× Standard_D8s_v5, autoscale)
    │   └── Node Pool: Workers (2–6× Standard_D4s_v5, autoscale)
    │
    ├── Azure Cache for Redis (Enterprise E10)
    │   ├── Redis Cluster (3 shards, zone-redundant)
    │   └── Persistence: RDB every 60s + AOF
    │
    ├── Azure Service Bus (Premium, 1 MU)
    │   ├── Topic: notifications
    │   ├── Topic: dashboard-updates
    │   └── Topic: system-alerts
    │
    ├── Azure SQL Database (Business Critical, 8 vCores)
    │   └── Geo-replication: secondary in paired region
    │
    ├── Azure Container Registry (ACR)
    │   └── Geo-replication enabled
    │
    ├── Azure Key Vault
    │   └── Secrets: connection strings, JWT signing keys
    │
    ├── Azure Monitor + Application Insights
    │   └── Log Analytics Workspace
    │
    ├── Azure Load Balancer (Standard)
    │   └── WebSocket affinity disabled (Redis backplane handles it)
    │
    └── Azure Private DNS Zone
        └── Internal service discovery
```

### Resource SKU Rationale

| Resource | SKU | Reason |
|---|---|---|
| Redis | Enterprise E10 | AOF persistence, 10 GB, cluster mode, zone-redundant |
| Service Bus | Premium | Dedicated capacity, VNET integration, large messages |
| SQL DB | Business Critical | In-memory OLTP for Hangfire tables, readable secondary |
| AKS Nodes (App) | Standard_D8s_v5 | 8 vCPU for SignalR connection density |

---

## 2. Kubernetes Deployment

### Namespace Layout

```
namespaces:
  - notification-engine       ← API pods, Hangfire workers
  - notification-engine-infra ← Redis Operator, monitoring
  - monitoring                ← Prometheus, Grafana
```

### API Deployment

```yaml
# infra/helm/notification-engine/templates/api-deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: notification-engine-api
  namespace: notification-engine
spec:
  replicas: 3
  strategy:
    type: RollingUpdate
    rollingUpdate:
      maxSurge: 1
      maxUnavailable: 0        # Zero-downtime deploys
  selector:
    matchLabels:
      app: notification-engine-api
  template:
    metadata:
      labels:
        app: notification-engine-api
        version: "{{ .Values.image.tag }}"
      annotations:
        prometheus.io/scrape: "true"
        prometheus.io/path: "/metrics"
        prometheus.io/port: "8080"
    spec:
      topologySpreadConstraints:
        - maxSkew: 1
          topologyKey: kubernetes.io/hostname
          whenUnsatisfiable: DoNotSchedule
          labelSelector:
            matchLabels:
              app: notification-engine-api
      containers:
        - name: api
          image: "{{ .Values.acr.loginServer }}/notification-engine-api:{{ .Values.image.tag }}"
          ports:
            - containerPort: 8080
              name: http
          env:
            - name: ASPNETCORE_ENVIRONMENT
              value: "{{ .Values.environment }}"
            - name: ConnectionStrings__Redis
              valueFrom:
                secretKeyRef:
                  name: notification-engine-secrets
                  key: redis-connection-string
            - name: ConnectionStrings__Sql
              valueFrom:
                secretKeyRef:
                  name: notification-engine-secrets
                  key: sql-connection-string
            - name: ServiceBus__ConnectionString
              valueFrom:
                secretKeyRef:
                  name: notification-engine-secrets
                  key: servicebus-connection-string
          resources:
            requests:
              memory: "512Mi"
              cpu: "250m"
            limits:
              memory: "2Gi"
              cpu: "2000m"
          livenessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 10
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 5
          lifecycle:
            preStop:
              exec:
                # Allow in-flight SignalR connections to drain
                command: ["/bin/sh", "-c", "sleep 15"]
```

### Hangfire Worker Deployment

Workers are deployed separately from API pods to prevent job processing from competing with SignalR connections for CPU.

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: notification-engine-worker
  namespace: notification-engine
spec:
  replicas: 2
  template:
    spec:
      containers:
        - name: worker
          image: "{{ .Values.acr.loginServer }}/notification-engine-worker:{{ .Values.image.tag }}"
          env:
            - name: Hangfire__WorkerCount
              value: "20"
            - name: Hangfire__Queues
              value: "critical,default,low"
          resources:
            requests:
              memory: "256Mi"
              cpu: "500m"
            limits:
              memory: "1Gi"
              cpu: "2000m"
```

### Ingress

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: notification-engine-ingress
  annotations:
    nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"      # WebSocket keep-alive
    nginx.ingress.kubernetes.io/proxy-send-timeout: "3600"
    nginx.ingress.kubernetes.io/proxy-http-version: "1.1"
    nginx.ingress.kubernetes.io/configuration-snippet: |
      proxy_set_header Upgrade $http_upgrade;
      proxy_set_header Connection "upgrade";
spec:
  rules:
    - host: api.notification-engine.example.com
      http:
        paths:
          - path: /hubs
            pathType: Prefix
            backend:
              service:
                name: notification-engine-api
                port:
                  number: 80
          - path: /
            pathType: Prefix
            backend:
              service:
                name: notification-engine-api
                port:
                  number: 80
```

### PodDisruptionBudget

```yaml
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: notification-engine-api-pdb
spec:
  minAvailable: 2
  selector:
    matchLabels:
      app: notification-engine-api
```

---

## 3. Terraform IaC

### Module Structure

```
infra/terraform/
├── main.tf
├── variables.tf
├── outputs.tf
├── terraform.tfvars.example
└── modules/
    ├── aks/
    │   ├── main.tf          # AKS cluster + node pools
    │   ├── variables.tf
    │   └── outputs.tf
    ├── redis/
    │   ├── main.tf          # Azure Cache for Redis Enterprise
    │   └── ...
    ├── servicebus/
    │   ├── main.tf          # Namespace, topics, subscriptions
    │   └── ...
    ├── sql/
    │   ├── main.tf          # Azure SQL, firewall rules, geo-rep
    │   └── ...
    └── keyvault/
        ├── main.tf          # Key Vault, access policies
        └── ...
```

### Core Resources

```hcl
# infra/terraform/modules/aks/main.tf

resource "azurerm_kubernetes_cluster" "main" {
  name                = "aks-notif-${var.environment}"
  location            = var.location
  resource_group_name = var.resource_group_name
  dns_prefix          = "notif-${var.environment}"
  kubernetes_version  = var.kubernetes_version

  default_node_pool {
    name                = "system"
    node_count          = 3
    vm_size             = "Standard_D4s_v5"
    os_disk_size_gb     = 128
    zones               = ["1", "2", "3"]
    only_critical_addons_enabled = true
  }

  identity {
    type = "SystemAssigned"
  }

  network_profile {
    network_plugin    = "azure"
    network_policy    = "azure"
    load_balancer_sku = "standard"
    outbound_type     = "userAssignedNATGateway"
  }

  oms_agent {
    log_analytics_workspace_id = var.log_analytics_workspace_id
  }

  workload_identity_enabled = true
  oidc_issuer_enabled       = true
}

resource "azurerm_kubernetes_cluster_node_pool" "app" {
  name                  = "app"
  kubernetes_cluster_id = azurerm_kubernetes_cluster.main.id
  vm_size               = "Standard_D8s_v5"
  node_count            = 2
  min_count             = 2
  max_count             = 10
  enable_auto_scaling   = true
  zones                 = ["1", "2", "3"]

  node_labels = {
    "workload" = "api"
  }

  node_taints = []
}

resource "azurerm_kubernetes_cluster_node_pool" "workers" {
  name                  = "workers"
  kubernetes_cluster_id = azurerm_kubernetes_cluster.main.id
  vm_size               = "Standard_D4s_v5"
  node_count            = 2
  min_count             = 2
  max_count             = 6
  enable_auto_scaling   = true

  node_labels = {
    "workload" = "worker"
  }

  node_taints = ["workload=worker:NoSchedule"]
}
```

```hcl
# infra/terraform/modules/redis/main.tf

resource "azurerm_redis_enterprise_cluster" "main" {
  name                = "redis-notif-${var.environment}"
  location            = var.location
  resource_group_name = var.resource_group_name
  sku_name            = "Enterprise_E10-2"
  zones               = ["1", "2", "3"]
}

resource "azurerm_redis_enterprise_database" "main" {
  cluster_id          = azurerm_redis_enterprise_cluster.main.id
  cluster_policy      = "EnterpriseCluster"   # Hash-slot distribution
  eviction_policy     = "NoEviction"           # Never evict stream data
  port                = 10000

  module {
    name = "RediSearch"                        # Optional: for notification search
  }
}
```

```hcl
# infra/terraform/modules/servicebus/main.tf

resource "azurerm_servicebus_namespace" "main" {
  name                = "sb-notif-${var.environment}"
  location            = var.location
  resource_group_name = var.resource_group_name
  sku                 = "Premium"
  capacity            = 1
  zone_redundant      = true
}

resource "azurerm_servicebus_topic" "notifications" {
  for_each = toset(["notifications", "dashboard-updates", "system-alerts"])

  name                         = each.key
  namespace_id                 = azurerm_servicebus_namespace.main.id
  max_size_in_megabytes        = 5120
  default_message_ttl          = "P7D"           # 7-day TTL
  duplicate_detection_history_time_window = "PT10M"
  requires_duplicate_detection = true
}

resource "azurerm_servicebus_subscription" "notification_engine" {
  for_each = azurerm_servicebus_topic.notifications

  name                = "notification-engine"
  topic_id            = each.value.id
  max_delivery_count  = 10
  lock_duration       = "PT5M"
  dead_lettering_on_message_expiration = true
}
```

### State Management

```hcl
# infra/terraform/main.tf
terraform {
  backend "azurerm" {
    resource_group_name  = "rg-terraform-state"
    storage_account_name = "stterraformstate"
    container_name       = "notification-engine"
    key                  = "terraform.tfstate"
  }
}
```

### Environments

```
infra/terraform/environments/
├── dev.tfvars
├── staging.tfvars
└── prod.tfvars
```

```hcl
# prod.tfvars
environment         = "prod"
location            = "eastus2"
kubernetes_version  = "1.29"
aks_app_min_count   = 3
aks_app_max_count   = 10
redis_sku           = "Enterprise_E10-2"
sql_sku_name        = "BC_Gen5_8"
servicebus_capacity = 2
```

---

## 4. CI/CD Pipeline

### Pipeline Overview (GitHub Actions)

```
PR Opened
  └─► ci.yml
        ├── lint (dotnet format --verify-no-changes)
        ├── build (dotnet build)
        ├── unit-tests (dotnet test --filter Category=Unit)
        └── integration-tests (docker-compose + dotnet test --filter Category=Integration)

Merge to main
  └─► deploy.yml
        ├── build-and-push (docker build → ACR)
        ├── deploy-staging (helm upgrade → staging AKS)
        ├── smoke-tests (k6 smoke scenario)
        └── deploy-prod (helm upgrade → prod AKS, manual approval gate)

Nightly
  └─► load-test.yml
        └── load-tests (NBomber → staging, publish results as artifact)
```

### CI Workflow

```yaml
# .github/workflows/ci.yml
name: CI

on:
  pull_request:
    branches: [main]

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    services:
      redis:
        image: redis:7.2
        ports: ["6379:6379"]
      sql:
        image: mcr.microsoft.com/mssql/server:2022-latest
        env:
          ACCEPT_EULA: Y
          SA_PASSWORD: ${{ secrets.SQL_SA_PASSWORD }}
        ports: ["1433:1433"]

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"

      - name: Restore
        run: dotnet restore

      - name: Format check
        run: dotnet format --verify-no-changes

      - name: Build
        run: dotnet build --no-restore --configuration Release

      - name: Unit tests
        run: |
          dotnet test tests/NotificationEngine.UnitTests \
            --no-build \
            --configuration Release \
            --logger "trx;LogFileName=unit-results.trx" \
            --collect:"XPlat Code Coverage"

      - name: Integration tests
        run: |
          dotnet test tests/NotificationEngine.IntegrationTests \
            --no-build \
            --configuration Release \
            --logger "trx;LogFileName=integration-results.trx"
        env:
          ConnectionStrings__Redis: "localhost:6379"
          ConnectionStrings__Sql: "Server=localhost;Database=NotifEngine_Test;User=sa;Password=${{ secrets.SQL_SA_PASSWORD }}"

      - name: Publish test results
        uses: dorny/test-reporter@v1
        if: always()
        with:
          name: Test Results
          path: "**/*.trx"
          reporter: dotnet-trx

      - name: Coverage report
        uses: codecov/codecov-action@v4
```

### Deploy Workflow

```yaml
# .github/workflows/deploy.yml
name: Deploy

on:
  push:
    branches: [main]

env:
  ACR_LOGIN_SERVER: ${{ secrets.ACR_LOGIN_SERVER }}
  IMAGE_TAG: ${{ github.sha }}

jobs:
  build-push:
    runs-on: ubuntu-latest
    outputs:
      image-tag: ${{ env.IMAGE_TAG }}
    steps:
      - uses: actions/checkout@v4

      - name: Azure login
        uses: azure/login@v2
        with:
          creds: ${{ secrets.AZURE_CREDENTIALS }}

      - name: ACR login
        run: az acr login --name ${{ secrets.ACR_NAME }}

      - name: Build & push API image
        run: |
          docker build \
            -f src/NotificationEngine.Api/Dockerfile \
            -t $ACR_LOGIN_SERVER/notification-engine-api:$IMAGE_TAG \
            -t $ACR_LOGIN_SERVER/notification-engine-api:latest \
            .
          docker push $ACR_LOGIN_SERVER/notification-engine-api:$IMAGE_TAG

      - name: Build & push Worker image
        run: |
          docker build \
            -f src/NotificationEngine.Worker/Dockerfile \
            -t $ACR_LOGIN_SERVER/notification-engine-worker:$IMAGE_TAG \
            .
          docker push $ACR_LOGIN_SERVER/notification-engine-worker:$IMAGE_TAG

  deploy-staging:
    needs: build-push
    runs-on: ubuntu-latest
    environment: staging
    steps:
      - uses: actions/checkout@v4

      - name: AKS credentials
        run: |
          az aks get-credentials \
            --resource-group rg-notif-staging \
            --name aks-notif-staging

      - name: Helm upgrade (staging)
        run: |
          helm upgrade --install notification-engine ./infra/helm/notification-engine \
            --namespace notification-engine \
            --create-namespace \
            --values infra/helm/notification-engine/values.staging.yaml \
            --set image.tag=${{ needs.build-push.outputs.image-tag }} \
            --wait \
            --timeout 5m

  deploy-prod:
    needs: [build-push, deploy-staging]
    runs-on: ubuntu-latest
    environment:
      name: production
      url: https://api.notification-engine.example.com
    steps:
      - name: Helm upgrade (prod)
        run: |
          helm upgrade --install notification-engine ./infra/helm/notification-engine \
            --namespace notification-engine \
            --values infra/helm/notification-engine/values.prod.yaml \
            --set image.tag=${{ needs.build-push.outputs.image-tag }} \
            --wait \
            --timeout 10m
```

---

## 5. Environment Configuration

### Configuration Hierarchy

```
appsettings.json                    ← Base defaults (non-secret)
  └─ appsettings.{Environment}.json ← Environment overrides
       └─ Environment Variables      ← Runtime injected (K8s secrets)
            └─ Azure Key Vault       ← Sensitive values (referenced by name)
```

### appsettings.json (base)

```json
{
  "Redis": {
    "StreamConsumerGroup": "notification-processor",
    "MaxStreamLength": 1000000,
    "ConsumerBatchSize": 50,
    "PendingClaimTimeoutMs": 30000,
    "PollIntervalMs": 100
  },
  "SignalR": {
    "KeepAliveInterval": "00:00:15",
    "ClientTimeoutInterval": "00:00:30",
    "MaximumReceiveMessageSize": 32768
  },
  "Hangfire": {
    "WorkerCount": 20,
    "Queues": ["critical", "default", "low"],
    "DashboardEnabled": true,
    "DashboardPath": "/hangfire"
  },
  "ServiceBus": {
    "Topics": {
      "Notifications": "notifications",
      "DashboardUpdates": "dashboard-updates",
      "SystemAlerts": "system-alerts"
    },
    "SubscriptionName": "notification-engine",
    "MaxConcurrentCalls": 16
  },
  "Notifications": {
    "MaxReplayMessages": 200,
    "PresenceTtlSeconds": 30,
    "MaxRetries": 5
  },
  "Observability": {
    "ServiceName": "notification-engine",
    "Prometheus": { "Enabled": true, "Path": "/metrics" }
  }
}
```

### Secret References (Key Vault)

Secrets are stored in Key Vault and mounted via [Azure Key Vault Provider for Secrets Store CSI Driver](https://learn.microsoft.com/azure/aks/csi-secrets-store-driver):

| Secret Name | Description |
|---|---|
| `redis-connection-string` | Redis Enterprise connection string + auth |
| `sql-connection-string` | Azure SQL connection string |
| `servicebus-connection-string` | Service Bus namespace SAS or managed identity |
| `jwt-signing-key` | RS256 private key for JWT validation |
| `hangfire-sql-connection-string` | May differ from app DB in high-load configs |

---

## 6. Scaling & Auto-Scaling

### Horizontal Pod Autoscaler

```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: notification-engine-api-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: notification-engine-api
  minReplicas: 3
  maxReplicas: 10
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: 60
    - type: Pods
      pods:
        metric:
          name: signalr_connections_active
        target:
          type: AverageValue
          averageValue: "15000"    # Scale out at 15k connections per pod
```

### KEDA for Stream-Based Scaling

[KEDA](https://keda.sh/) scales consumer workers based on Redis Stream lag:

```yaml
apiVersion: keda.sh/v1alpha1
kind: ScaledObject
metadata:
  name: stream-consumer-scaler
spec:
  scaleTargetRef:
    name: notification-engine-worker
  minReplicaCount: 2
  maxReplicaCount: 8
  triggers:
    - type: redis-streams
      metadata:
        address: redis-notif-prod.redis.cache.windows.net:10000
        stream: stream:notifications:*
        consumerGroup: notification-processor
        lagCount: "1000"           # Scale up when lag exceeds 1000 messages
```

---

## 7. Networking & Security

### Network Policy

```yaml
# Only allow ingress from ingress controller and monitoring
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: notification-engine-netpol
  namespace: notification-engine
spec:
  podSelector:
    matchLabels:
      app: notification-engine-api
  policyTypes:
    - Ingress
    - Egress
  ingress:
    - from:
        - namespaceSelector:
            matchLabels:
              name: ingress-nginx
        - namespaceSelector:
            matchLabels:
              name: monitoring
  egress:
    - to:
        - namespaceSelector: {}   # Allow all egress within cluster
    - ports:
        - port: 443               # Azure services (HTTPS)
        - port: 10000             # Redis Enterprise
        - port: 5671              # Service Bus AMQP TLS
```

### Managed Identity

API pods use [Azure Workload Identity](https://azure.github.io/azure-workload-identity/) to authenticate against Azure services without storing credentials:

```csharp
// No connection strings needed for Service Bus in production
builder.Services.AddAzureClients(clients =>
{
    clients.AddServiceBusClient(
        new Uri($"https://{builder.Configuration["ServiceBus:Namespace"]}.servicebus.windows.net"))
        .WithCredential(new DefaultAzureCredential());
});
```

---

## 8. Disaster Recovery

### RTO / RPO Targets

| Environment | RTO | RPO |
|---|---|---|
| Production | 15 minutes | 30 seconds |
| Staging | 2 hours | 1 hour |

### Backup Strategy

| Resource | Backup Method | Frequency | Retention |
|---|---|---|---|
| Redis | RDB snapshots + AOF | RDB every 60s | 7 days |
| Azure SQL | Automated backups | Continuous (log) / Daily (full) | 35 days |
| Service Bus | Geo-DR pairing | Active/passive failover | N/A |

### Failover Runbook

See [RUNBOOK.md](./RUNBOOK.md#regional-failover) for the step-by-step failover procedure.

### Multi-Region Considerations

The system is designed for single-region primary with geo-redundant storage. A full active-active multi-region deployment is possible but requires:

1. Redis Enterprise Active-Active (CRDTs) — additional licensing
2. Azure Service Bus Geo-DR failover procedures
3. Azure SQL active geo-replication with read-write endpoint failover
4. Traffic Manager / Front Door for DNS-level routing
