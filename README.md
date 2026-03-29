<!-- PROJECT BADGES -->
<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=.net&logoColor=white" alt=".NET 8.0" />
  <img src="https://img.shields.io/badge/Clean_Architecture-FF6B6B?style=for-the-badge&logo=architecture&logoColor=white" alt="Clean Architecture" />
  <img src="https://img.shields.io/badge/SignalR-FF6B35?style=for-the-badge&logo=signalr&logoColor=white" alt="SignalR" />
  <img src="https://img.shields.io/badge/Redis-DC382D?style=for-the-badge&logo=redis&logoColor=white" alt="Redis" />
  <img src="https://img.shields.io/badge/Hangfire-000000?style=for-the-badge&logo=hangfire&logoColor=white" alt="Hangfire" />
</p>

<h1 align="center">🚀 Notification Engine</h1>

<p align="center">
  A production-ready .NET 8 notification system built with Clean Architecture, featuring real-time SignalR hubs, Redis streams, and Azure Service Bus integration.
</p>

<p align="center">
  <a href="#features">Features</a> •
  <a href="#architecture">Architecture</a> •
  <a href="#getting-started">Getting Started</a> •
  <a href="#api-endpoints">API Endpoints</a> •
  <a href="#configuration">Configuration</a>
</p>

---

## ✨ Features

| Feature | Description |
|---------|-------------|
| 🏗️ **Clean Architecture** | Domain-Driven Design with proper layer separation |
| 📡 **Real-time Notifications** | SignalR hubs with Redis backplane for scaled deployments |
| 🔴 **Redis Streams** | Durable event backbone with consumer groups & exactly-once semantics |
| 🚌 **Azure Service Bus** | Cross-service communication with external event ingestion |
| 📬 **Transactional Outbox** | Reliable event publishing pattern with SQL Server storage |
| ⚡ **Hangfire Jobs** | Background job processing with SQL storage & priority queues |
| 👥 **Presence Tracking** | User online/offline status with Redis sorted sets |
| 📊 **Observability** | OpenTelemetry tracing with Serilog structured logging |
| 🔐 **JWT Authentication** | Secure API access with claim-based authorization |

---

## 🏛️ Architecture

### Solution Structure

```
NotificationEngine/
├── src/
│   ├── NotificationEngine.Domain/          # Core domain layer
│   ├── NotificationEngine.Application/     # Application layer (MediatR)
│   ├── NotificationEngine.Infrastructure/ # Infrastructure layer
│   └── NotificationEngine.Api/             # API layer (ASP.NET Core)
├── tests/                                  # Test projects
├── docker-compose.yml                      # Local infrastructure
└── README.md
```

### Layer Dependencies

```mermaid
graph TD
    API[API Layer<br/>(ASP.NET Core)] --> Application
    API --> Infrastructure
    Application --> Domain
    Infrastructure --> Domain
    Infrastructure --> Application
    
    style API fill:#4ECDC4,stroke:#333,color:#000
    style Application fill:#45B7D1,stroke:#333,color:#000
    style Infrastructure fill:#96CEB4,stroke:#333,color:#000
    style Domain fill:#FFEAA7,stroke:#333,color:#000
```

### Event Flow

```mermaid
sequenceDiagram
    participant Client as Client App
    participant API as API Layer
    participant MediatR as MediatR
    participant DB as SQL Server
    participant Outbox as Outbox Table
    participant Hangfire as Hangfire
    participant Redis as Redis Streams
    participant SignalR as SignalR Hub
    participant SB as Azure Service Bus

    Client->>API: POST /notifications
    API->>MediatR: Send Command
    MediatR->>DB: SaveChanges
    DB->>Outbox: Write Domain Event
    DB-->>API: Return
    
    Note over Hangfire: Every 5 seconds
    Hangfire->>Outbox: Read unpublished events
    Outbox-->>Hangfire: Events
    Hangfire->>Redis: XADD to stream
    Redis-->>Hangfire: Acknowledged
    
    Note over Redis: Stream Consumer
    Redis->>MediatR: Publish Domain Event
    MediatR->>SignalR: Send to Client
    
    SignalR->>Client: Real-time Update
    
    Note over API,SB: Service Bus Integration
    SB->>API: External Event
    API->>Redis: Route to Internal Stream
```

### Infrastructure Components

```mermaid
graph TB
    subgraph "API Layer"
        API[ASP.NET Core API]
        Hub[SignalR Hubs]
        Dashboard[DashboardHub]
    end
    
    subgraph "Application Layer"
        MediatR[MediatR]
        Pipeline[Pipeline Behaviors]
        Handlers[Event Handlers]
    end
    
    subgraph "Infrastructure"
        EF[EF Core / SQL Server]
        Redis[Redis]
        Streams[Redis Streams]
        Presence[Presence Tracker]
        SB[Azure Service Bus]
        Hangfire[Hangfire]
    end
    
    API --> Hub
    Hub --> Dashboard
    API --> MediatR
    MediatR --> Pipeline
    Pipeline --> Handlers
    MediatR --> EF
    Redis --> Streams
    Redis --> Presence
    EF --> Hangfire
    SB --> Redis
```

---

## 🚀 Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop)

### Quick Start

1. **Clone and start infrastructure:**
   ```bash
   docker-compose up -d
   ```

2. **Update connection strings** in `src/NotificationEngine.Api/appsettings.Development.json`:
   ```json
   {
     "ConnectionStrings": {
       "Sql": "Server=localhost;Database=NotificationEngine;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=true",
       "Redis": "localhost:6379"
     },
     "Authentication": {
       "Authority": "https://your-identity-provider.com",
       "Audience": "notification-engine-api"
     }
   }
   ```

3. **Run the API:**
   ```bash
   cd src/NotificationEngine.Api
   dotnet run
   ```

---

## 📡 API Endpoints

### Health Checks
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/health` | Basic health check |
| GET | `/health/ready` | Readiness check |
| GET | `/health/live` | Liveness check |

### SignalR Hubs
| Hub | Endpoint | Description |
|-----|----------|-------------|
| Dashboard | `/hubs/dashboard` | Real-time notification hub |

### Dashboards
| Endpoint | Description |
|----------|-------------|
| `/hangfire` | Hangfire job dashboard (requires Admin role) |
| `/swagger` | API documentation |

---

## ⚙️ Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `ConnectionStrings:Sql` | SQL Server connection | `localhost` |
| `ConnectionStrings:Redis` | Redis connection | `localhost:6379` |
| `ConnectionStrings:ServiceBus` | Azure Service Bus | - |
| `Authentication:Authority` | JWT issuer | - |
| `Authentication:Audience` | JWT audience | - |

### Queue Priority Order

Hangfire processes jobs in this priority order:
1. `critical` - High priority
2. `default` - Normal priority  
3. `low` - Low priority

---

## 📦 NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| MediatR | 12.2.0 | CQRS & Mediator |
| FluentValidation | 11.9.0 | Request validation |
| EF Core | 8.0.0 | ORM & Outbox pattern |
| Serilog | 10.0.0 | Structured logging |
| SignalR | 8.0.0 | Real-time communication |
| StackExchange.Redis | 2.7.10 | Redis client |
| Hangfire | 1.8.6 | Background jobs |
| OpenTelemetry | 1.7.0 | Observability |

---

## 🔧 Development

### Building

```bash
dotnet build
```

### Running Tests

```bash
dotnet test
```

---

## 📄 License

MIT License - see [LICENSE](LICENSE) for details.

---

<p align="center">
  Built with ❤️ using .NET 8
</p>
