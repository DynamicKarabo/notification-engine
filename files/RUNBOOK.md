# Runbook — Real-Time Notification & Live Dashboard Engine

## Table of Contents

1. [On-Call Overview](#1-on-call-overview)
2. [Alerting Reference](#2-alerting-reference)
3. [Runbook Procedures](#3-runbook-procedures)
   - [High Stream Consumer Lag](#high-stream-consumer-lag)
   - [SignalR Connection Spike](#signalr-connection-spike)
   - [Redis Unavailable](#redis-unavailable)
   - [High Hangfire Dead-Letter Count](#high-hangfire-dead-letter-count)
   - [Service Bus Throttling](#service-bus-throttling)
   - [Regional Failover](#regional-failover)
4. [Deployment Procedures](#4-deployment-procedures)
5. [Scaling Procedures](#5-scaling-procedures)
6. [Maintenance Procedures](#6-maintenance-procedures)

---

## 1. On-Call Overview

**On-call rotation:** PagerDuty — `notification-engine-oncall`  
**Escalation path:** L1 (On-Call Engineer) → L2 (Platform Team Lead) → L3 (Engineering Director)  
**SLOs:**
- API availability: 99.9% monthly (max 43.8 min downtime)
- Notification P99 latency: < 500ms end-to-end
- Stream consumer lag: < 5,000 messages sustained

**Key dashboards:**
- Grafana: `https://grafana.internal/d/notification-engine`
- Hangfire: `https://api.notification-engine.example.com/hangfire`
- Azure Monitor: Resource Group `rg-notif-prod`

---

## 2. Alerting Reference

| Alert | Threshold | Severity | PD Policy |
|---|---|---|---|
| Stream consumer lag high | > 5,000 messages for 5 min | P2 | `notification-engine-oncall` |
| Stream consumer lag critical | > 20,000 messages for 2 min | P1 | `notification-engine-oncall` |
| SignalR connections per pod high | > 18,000 per pod | P3 | Slack `#platform-alerts` |
| Redis CPU high | > 80% for 10 min | P2 | `notification-engine-oncall` |
| Redis unavailable | Health check failing | P1 | `notification-engine-oncall` |
| Hangfire DLQ count high | > 100 failed jobs | P2 | Slack `#platform-alerts` |
| Hangfire DLQ count critical | > 500 failed jobs | P1 | `notification-engine-oncall` |
| API pod crash loop | 3 restarts in 5 min | P1 | `notification-engine-oncall` |
| SQL connection pool exhausted | > 95% pool | P2 | `notification-engine-oncall` |
| Service Bus throttled | 429 rate > 1% | P3 | Slack `#platform-alerts` |

---

## 3. Runbook Procedures

### High Stream Consumer Lag

**Alert:** `stream_consumer_lag > 5000`

**Likely causes:**
- Consumer group workers are too few for current ingestion rate
- A slow MediatR handler is blocking consumer throughput
- Redis connection issues reducing consumer poll rate

**Investigation:**

```bash
# Check current lag per stream
redis-cli XPENDING stream:notifications:* notification-processor - + 10

# Check consumer group info
redis-cli XINFO GROUPS stream:notifications:*

# Check worker pod count and CPU
kubectl get pods -n notification-engine -l app=notification-engine-worker
kubectl top pods -n notification-engine -l app=notification-engine-worker

# Check slow handlers in logs
kubectl logs -n notification-engine -l app=notification-engine-worker \
  | grep "Performance" | grep -v "< 500ms"
```

**Resolution:**

1. **Scale workers immediately:**
   ```bash
   kubectl scale deployment notification-engine-worker \
     -n notification-engine --replicas=6
   ```

2. If lag continues growing, identify and investigate the slow handler from logs. A handler taking > 2s indicates an N+1 query or external call that should be moved to a Hangfire job.

3. If Redis is the bottleneck (CPU > 80%), consider scaling the Redis cluster or temporarily increasing `MAXLEN` trimming aggressiveness.

4. Update KEDA `lagCount` threshold if sustained higher load is expected.

---

### SignalR Connection Spike

**Alert:** `signalr_connections_active > 18000 per pod`

**Likely causes:**
- Organic growth in connected clients
- Clients not disconnecting properly (browser tab not closed)
- Reconnect storm after a brief outage

**Investigation:**

```bash
# Check connections per pod
kubectl exec -n notification-engine \
  $(kubectl get pod -l app=notification-engine-api -o name | head -1) \
  -- curl -s http://localhost:8080/metrics | grep signalr_connections_active

# Check presence tracker for stale entries
redis-cli ZCOUNT presence:users -inf $(date -d '5 minutes ago' +%s)
```

**Resolution:**

1. HPA should trigger within 3 minutes. Verify:
   ```bash
   kubectl get hpa -n notification-engine
   kubectl describe hpa notification-engine-api-hpa -n notification-engine
   ```

2. If HPA is not scaling (e.g., max replicas reached), manually increase:
   ```bash
   kubectl patch hpa notification-engine-api-hpa -n notification-engine \
     -p '{"spec":{"maxReplicas":15}}'
   ```

3. If it is a reconnect storm, check for recent deployments or Redis blips that may have triggered mass disconnection.

4. Run presence cleanup to purge stale Redis entries:
   ```bash
   # Trigger the Hangfire recurring job immediately
   curl -X POST https://api.notification-engine.example.com/internal/jobs/presence-cleanup \
     -H "Authorization: Bearer $ADMIN_TOKEN"
   ```

---

### Redis Unavailable

**Alert:** `/health/ready` returning unhealthy due to Redis

**This is a P1 incident.**

**Investigation:**

```bash
# Check Redis status in Azure Portal
az redis show --name redis-notif-prod --resource-group rg-notif-prod \
  --query "provisioningState"

# Check pod connectivity
kubectl exec -n notification-engine \
  $(kubectl get pod -l app=notification-engine-api -o name | head -1) \
  -- redis-cli -h $REDIS_HOST -p 10000 --tls PING
```

**Resolution:**

1. **Immediate degraded mode:** The circuit breaker should already have tripped. Verify clients are falling back to poll-based API (`/api/notifications/pending`).

2. If Redis is in a failover (Azure maintenance), wait for automatic failover (typically < 30 seconds on Enterprise tier). Monitor Azure Service Health.

3. If Redis cluster has data corruption:
   ```bash
   # Failover to replica
   az redis force-reboot --name redis-notif-prod \
     --resource-group rg-notif-prod --reboot-type PrimaryNode
   ```

4. Once Redis recovers, the circuit breaker will close automatically after 5 successful health probes. Verify with:
   ```bash
   kubectl logs -n notification-engine -l app=notification-engine-api \
     | grep "circuit" | tail -20
   ```

5. Stream consumers will automatically recover via PEL replay. Expect elevated consumer lag for 2-5 minutes post-recovery.

---

### High Hangfire Dead-Letter Count

**Alert:** `hangfire_jobs_failed_total > 100`

**Investigation:**

1. Open Hangfire dashboard: `https://api.notification-engine.example.com/hangfire`
2. Navigate to **Failed** jobs tab
3. Identify the job type and exception message

```bash
# Query SQL directly for pattern
sqlcmd -S $SQL_HOST -d NotificationEngine -Q "
SELECT TOP 20 
  [Job].[InvocationData], 
  [State].[Reason], 
  [State].[CreatedAt]
FROM [Hangfire].[Job]
JOIN [Hangfire].[State] ON [Job].[StateId] = [State].[Id]
WHERE [Job].[StateName] = 'Failed'
ORDER BY [State].[CreatedAt] DESC
"
```

**Common causes and fixes:**

| Exception | Cause | Fix |
|---|---|---|
| `SmtpException: Connection refused` | Email server unreachable | Check SendGrid/SMTP credentials; re-queue after fix |
| `SqlException: Timeout` | DB overloaded | Scale SQL, then re-queue |
| `HttpRequestException: 503` | External webhook endpoint down | Notify customer; jobs will retry automatically |
| `JsonException` | Schema mismatch | Deploy contract fix; re-queue manually |

**Re-queue all failed jobs of a type:**
```bash
# Via Hangfire dashboard UI: Failed → Select All of type → Requeue
# Or via API:
curl -X POST https://api.notification-engine.example.com/internal/hangfire/requeue-failed \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -d '{"jobType": "EmailNotificationJob"}'
```

---

### Service Bus Throttling

**Alert:** Service Bus 429 error rate > 1%

**Likely causes:**
- Message burst from upstream services
- Insufficient Service Bus capacity units

**Resolution:**

1. Polly retry will handle transient bursts automatically. Check if the alert self-resolves within 5 minutes.

2. If sustained, scale Service Bus:
   ```bash
   az servicebus namespace update \
     --name sb-notif-prod \
     --resource-group rg-notif-prod \
     --capacity 2
   ```

3. If caused by a single upstream service flooding topics, identify and throttle that service at the producer side.

---

### Regional Failover

**This procedure is for full regional outage. Trigger only after P0 declaration.**

**Prerequisites:**
- Azure SQL geo-replication secondary is healthy
- ACR geo-replication is in sync

**Steps:**

1. **Confirm primary region failure** via Azure Service Health dashboard.

2. **Promote SQL secondary:**
   ```bash
   az sql db replica set-primary \
     --name NotificationEngine \
     --resource-group rg-notif-prod-secondary \
     --server sql-notif-prod-secondary
   ```

3. **Update AKS in secondary region** (pre-provisioned, scaled to 0):
   ```bash
   az aks scale \
     --name aks-notif-prod-secondary \
     --resource-group rg-notif-prod-secondary \
     --node-count 3 \
     --nodepool-name app
   ```

4. **Deploy latest image** to secondary AKS:
   ```bash
   helm upgrade --install notification-engine ./infra/helm/notification-engine \
     --namespace notification-engine \
     --values infra/helm/notification-engine/values.prod-secondary.yaml \
     --set image.tag=$(az acr repository show-tags \
       --name acr-notif-prod \
       --repository notification-engine-api \
       --orderby time_desc --top 1 --query '[0]' -o tsv)
   ```

5. **Update DNS / Traffic Manager** to point to secondary region load balancer.

6. **Notify stakeholders** — stream state will not be available from primary Redis. Clients will reconnect and receive only new events. Missed events are available from the SQL outbox if needed.

---

## 4. Deployment Procedures

### Standard Deployment (via CI/CD)

Normal deployments are automated. Push to `main` → GitHub Actions → Helm upgrade with zero downtime.

### Emergency Hotfix Deployment

```bash
# Build and push hotfix image
docker build -t $ACR/notification-engine-api:hotfix-$(git rev-parse --short HEAD) .
docker push $ACR/notification-engine-api:hotfix-$(git rev-parse --short HEAD)

# Deploy immediately (bypasses staging)
helm upgrade notification-engine ./infra/helm/notification-engine \
  --namespace notification-engine \
  --reuse-values \
  --set image.tag=hotfix-$(git rev-parse --short HEAD)
```

### Rollback

```bash
# Rollback to previous Helm revision
helm rollback notification-engine -n notification-engine

# Or to a specific revision
helm history notification-engine -n notification-engine
helm rollback notification-engine 5 -n notification-engine
```

---

## 5. Scaling Procedures

### Manual Scale API Pods

```bash
kubectl scale deployment notification-engine-api \
  -n notification-engine --replicas=8
```

### Manual Scale Workers

```bash
kubectl scale deployment notification-engine-worker \
  -n notification-engine --replicas=6
```

### Database Migration (zero downtime)

```bash
# Run migrations as a pre-deploy Kubernetes Job
kubectl apply -f infra/k8s/db-migration-job.yaml
kubectl wait --for=condition=complete job/db-migration --timeout=120s -n notification-engine
```

---

## 6. Maintenance Procedures

### Redis Stream Cleanup

```bash
# Trim a specific stream to 500,000 entries
redis-cli XTRIM stream:notifications:tenant-123 MAXLEN = 500000

# Check stream memory usage
redis-cli MEMORY USAGE stream:notifications:tenant-123
```

### Outbox Table Maintenance

```sql
-- Purge processed outbox messages older than 7 days
DELETE FROM OutboxMessage
WHERE ProcessedAt IS NOT NULL
  AND ProcessedAt < DATEADD(DAY, -7, GETUTCDATE());
```

### Hangfire Storage Cleanup

Hangfire automatically purges old job records based on retention settings in `GlobalConfiguration`. Default retention:
- Succeeded: 24 hours
- Failed: 30 days

Manually trigger cleanup:
```bash
curl -X POST https://api.notification-engine.example.com/internal/hangfire/cleanup \
  -H "Authorization: Bearer $ADMIN_TOKEN"
```

### Certificate Rotation (JWT Signing Key)

1. Generate new RSA key pair
2. Store in Key Vault under new version
3. Update `jwt-signing-key` secret reference in K8s
4. Rolling restart: `kubectl rollout restart deployment notification-engine-api -n notification-engine`
5. Both old and new keys should be valid during a 15-minute transition window (token issuers need updating simultaneously)
