# Notification Engine - Comprehensive Project Documentation

## Table of Contents
1. [Project Overview](#project-overview)
2. [Architecture Decisions](#architecture-decisions)
3. [Implementation Phases](#implementation-phases)
4. [Technical Stack](#technical-stack)
5. [Key Implementation Details](#key-implementation-details)
6. [Challenges & Solutions](#challenges--solutions)
7. [Testing & Verification](#testing--verification)

---

## Project Overview

A **.NET 8 Clean Architecture Notification Engine** built through sequential implementation prompts. The system provides real-time notifications via SignalR, background job processing with Hangfire, event-driven messaging via Redis Streams and Azure Service Bus, and follows the Transactional Outbox Pattern for reliable event publishing.

### Core Capabilities
- Real-time WebSocket notifications via SignalR
- Background job processing with Hangfire
- Event-driven architecture with Redis Streams
- Azure Service Bus integration for cloud messaging
- Transactional Outbox Pattern for reliable event publishing
- Presence tracking via Redis sorted sets
- JWT authentication with custom token generation for testing
- OpenTelemetry distributed tracing
- Serilog structured logging

---

## Architecture Decisions

### Clean Architecture Layers

```
┌─────────────────────────────────────────┐
│           NotificationEngine.Api        │  ← Entry point, SignalR Hubs, JWT Config
├─────────────────────────────────────────┤
│      NotificationEngine.Application     │  ← Use cases, Interfaces, DTOs
├─────────────────────────────────────────┤
│     NotificationEngine.Infrastructure   │  ← EF Core, Redis, Hangfire, Service Bus
├─────────────────────────────────────────┤
│        NotificationEngine.Domain        │  ← Entities, Value Objects, Events
└─────────────────────────────────────────┘
```

### Layer Responsibilities

| Layer | Responsibility | Key Components |
|-------|---------------|----------------|
| **Domain** | Business rules, entities, events | `IAggregateRoot`, `DomainEventBase`, Entities |
| **Application** | Use cases, port definitions | MediatR handlers, Job interfaces, Messaging abstractions |
| **Infrastructure** | External service integrations | EF Core, Redis, Hangfire, Service Bus, SignalR services |
| **API** | HTTP/WebSocket entry point | SignalR Hubs, JWT auth, Swagger, Health checks |

### Key Architectural Patterns

1. **CQRS with MediatR** - Command/Query separation
2. **Transactional Outbox** - Reliable event publishing
3. **Repository Pattern** - Data access abstraction
4. **Domain Events** - Loose coupling between aggregates
5. **Background Services** - Long-running processes for message consumption

---

## Implementation Phases

### Prompt 1: Project Setup & Domain Layer
**Status:** ✅ Complete

**What was implemented:**
- Solution structure with 4 projects (Domain, Application, Infrastructure, API)
- Domain entities: `IAggregateRoot`, `DomainEventBase`
- MediatR integration for CQRS
- Serilog structured logging with console sink
- OpenTelemetry distributed tracing
- JWT Bearer authentication setup
- Health check endpoints

**Key decisions:**
- Used MediatR 12.2.0 (latest stable at time)
- Domain events implement `MediatR.INotification`
- `DomainEventBase` is a record (not class) for immutability

### Prompt 2: EF Core & Transactional Outbox
**Status:** ✅ Complete

**What was implemented:**
- `ApplicationDbContext` with EF Core + SQL Server
- `OutboxMessage` entity for transactional outbox pattern
- Domain event handling in `SaveChangesAsync`
- `OutboxPublisherJob` for processing outbox messages

**Key decisions:**
- Outbox pattern ensures events are published only after successful transactions
- `IdempotencyKey` prevents duplicate event processing
- `ProcessedAt` index for efficient querying of pending messages

### Prompt 3: MediatR Pipeline Behaviors
**Status:** ✅ Complete

**What was implemented:**
- Validation behavior using FluentValidation
- Logging behavior for commands/queries
- Performance monitoring behavior
- Transaction behavior for commands

**Key decisions:**
- Behaviors execute in registration order
- Validation fails fast before reaching handler
- Transaction behavior wraps commands in DB transactions

### Prompt 4: SignalR Hub & Redis Backplane
**Status:** ✅ Complete

**What was implemented:**
- `DashboardHub` with presence tracking
- `RedisPresenceTracker` using sorted sets
- `RedisGroupMembership` for tenant-based grouping
- Custom `UserIdProvider` for claim-based user identification
- SignalR Redis backplane for scale-out

**Key decisions:**
- Presence tracked via Redis sorted sets (score = Unix timestamp)
- User identified by `NameIdentifier` claim
- Tenant grouping via `tenant_id` claim
- 30-second presence timeout for stale connections

### Prompt 5: Redis Streams & Azure Service Bus
**Status:** ✅ Complete

**What was implemented:**
- `RedisStreamProducer` for publishing events
- `RedisStreamConsumer` as hosted service
- `ServiceBusSubscriber` for Azure integration
- `SignalRNotificationService` for real-time delivery

**Key decisions:**
- Redis Streams for local development
- Azure Service Bus for production
- Consumer groups for load balancing
- Automatic reconnection policies

### Prompt 6: Background Jobs with Hangfire
**Status:** ✅ Complete

**What was implemented:**
- Hangfire dashboard at `/hangfire`
- `OutboxPublisherJob` as recurring job (every 5 minutes)
- `EmailNotificationJob` for email delivery
- `SmsNotificationJob` for SMS delivery
- Custom `HangfireAuthFilter` for dashboard access

**Key decisions:**
- SQL Server storage for Hangfire persistence
- Multiple job queues: critical, default, low
- Worker count = ProcessorCount * 5

---

## Technical Stack

### Runtime & Framework
| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 8.0 | Runtime |
| ASP.NET Core | 8.0 | Web framework |
| C# | 12 | Language |

### Dependencies

#### Domain Layer
| Package | Version | Purpose |
|---------|---------|---------|
| MediatR | 12.2.0 | CQRS implementation |

#### Application Layer
| Package | Version | Purpose |
|---------|---------|---------|
| MediatR | 12.2.0 | CQRS implementation |
| FluentValidation | - | Input validation |

#### Infrastructure Layer
| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.EntityFrameworkCore.SqlServer | 8.0 | Database access |
| StackExchange.Redis | 2.7.10 | Redis client |
| Azure.Messaging.ServiceBus | 7.x | Azure Service Bus |
| Hangfire.AspNetCore | 1.8.6 | Background jobs |
| Hangfire.SqlServer | 1.8.6 | Hangfire storage |

#### API Layer
| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.AspNetCore.Authentication.JwtBearer | 8.0.0 | JWT auth |
| System.IdentityModel.Tokens.Jwt | 7.5.1 | Token generation (dev) |
| Microsoft.AspNetCore.SignalR.StackExchangeRedis | 8.0.0 | Redis backplane |
| Swashbuckle.AspNetCore | 6.6.2 | Swagger UI |
| Serilog.AspNetCore | 10.0.0 | Logging |
| OpenTelemetry.Extensions.Hosting | 1.7.0 | Distributed tracing |

---

## Key Implementation Details

### SignalR Hub Configuration

```csharp
// Program.cs
builder.Services.AddSignalR()
    .AddStackExchangeRedis(redisConnectionString, options =>
    {
        options.Configuration.ChannelPrefix = RedisChannel.Literal("signalr");
        options.Configuration.ConnectRetry = 5;
        options.Configuration.ReconnectRetryPolicy = new LinearRetry(500);
    });
```

### JWT Authentication (Development)

```csharp
// Dev token endpoint generates test tokens
app.MapGet("/dev/token", (string? userId, string? tenantId) =>
{
    // Creates JWT with:
    // - NameIdentifier claim (user ID)
    // - tenant_id claim
    // - test-issuer
    // - notification-engine-api audience
});
```

### Presence Tracking

```csharp
// Redis sorted set structure
Key: "presence:users"
Score: Unix timestamp (last seen)
Value: User ID

// Connection tracking
Key: "presence:connections:{userId}"
Type: Set
Value: Connection IDs
```

### Outbox Pattern Flow

```
1. Domain event raised → 2. SaveChangesAsync called
                           ↓
3. OutboxMessage created → 4. Transaction committed
                           ↓
5. Hangfire job polls → 6. Message published to Redis/ServiceBus
                        ↓
7. OutboxMessage marked as processed
```

---

## Challenges & Solutions

### Challenge 1: Package Version Conflicts
**Problem:** MediatR.Extensions.Microsoft.DependencyInjection incompatible with MediatR 12.x

**Solution:** Removed the package, MediatR 12+ has built-in DI support

### Challenge 2: Domain Events Not Implementing INotification
**Problem:** MediatR requires `INotification` for publish operations

**Solution:** Made `DomainEventBase` implement `MediatR.INotification`

### Challenge 3: Swagger UI Not Loading
**Problem:** Static files for Swagger not being served

**Solution:** 
- Set `RoutePrefix = ""` to serve at root
- Fixed `SwaggerEndpoint` path to `/swagger/v1/swagger.json`

### Challenge 4: Missing OutboxMessages Table
**Problem:** Database existed but table wasn't created

**Solution:** Manually created table via SQL command:
```sql
CREATE TABLE OutboxMessages (
    Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    OccurredOn DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    Type NVARCHAR(256) NOT NULL,
    Payload NVARCHAR(MAX) NOT NULL,
    ProcessedAt DATETIME2 NULL,
    Error NVARCHAR(1000) NULL,
    IdempotencyKey NVARCHAR(128) NOT NULL
);
```

### Challenge 5: SignalR Authentication
**Problem:** JWT token validation failing with Azure AD configuration

**Solution:** 
- Configured symmetric key for development testing
- Token includes `NameIdentifier` and `tenant_id` claims
- `OnMessageReceived` event extracts token from query string for WebSocket

### Challenge 6: CORS Issues with Test Page
**Problem:** Browser blocking cross-origin requests from HTTP server to API

**Solution:** Added CORS configuration:
```csharp
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});
```

### Challenge 7: Port Conflicts
**Problem:** Previous API instance still running on port 5270

**Solution:** Kill processes with `fuser -k 5270/tcp`

---

## Testing & Verification

### Infrastructure Status
| Service | Port | Status |
|---------|------|--------|
| SQL Server | 1433 | ✅ Running |
| Redis | 6379 | ✅ Running |
| Azurite (Storage) | 10000-10002 | ✅ Running |

### API Endpoints
| Endpoint | Method | Purpose | Status |
|----------|--------|---------|--------|
| `/health` | GET | Health check | ✅ |
| `/health/ready` | GET | Readiness probe | ✅ |
| `/health/live` | GET | Liveness probe | ✅ |
| `/dev/token` | GET | Test JWT generation | ✅ |
| `/swagger` | GET | API documentation | ✅ |
| `/hangfire` | GET | Job dashboard | ✅ |
| `/hubs/dashboard` | WS | SignalR hub | ✅ |

### SignalR Test Results

**Test 1: Connection & Presence**
- ✅ JWT token generated with correct claims
- ✅ WebSocket connection established
- ✅ Presence recorded in Redis sorted set
- ✅ User ID extracted from JWT claims
- ✅ Tenant ID extracted from JWT claims
- ✅ Hub logs connection events

**Redis Presence Data:**
```
Key: presence:users
Value: browser-user
Score: 1774806989 (Unix timestamp)
```

**API Log:**
```
Client connected. UserId=browser-user ConnectionId=QwvnBC5zcersERhY8Ogprw TenantId=test-tenant-1
```

---

## File Structure

```
notification-engine/
├── src/
│   ├── NotificationEngine.Api/
│   │   ├── Hubs/
│   │   │   ├── DashboardHub.cs          # SignalR hub with presence
│   │   │   └── CustomUserIdProvider.cs  # Claim-based user ID
│   │   ├── Contracts/
│   │   │   └── SignalR/
│   │   │       └── PresencePayload.cs   # DTOs for SignalR
│   │   ├── Program.cs                   # App configuration
│   │   ├── HangfireAuthFilter.cs        # Dashboard auth
│   │   └── appsettings.Development.json # Dev config
│   │
│   ├── NotificationEngine.Application/
│   │   ├── Abstractions/
│   │   │   ├── Jobs/                    # IOutboxPublisherJob, etc.
│   │   │   ├── Messaging/              # IEventProducer, etc.
│   │   │   ├── Presence/               # IPresenceTracker, etc.
│   │   │   └── Presistence/
│   │   │       └── IApplicationDbContext.cs
│   │   └── DependencyInjection.cs
│   │
│   ├── NotificationEngine.Infrastructure/
│   │   ├── Jobs/
│   │   │   ├── OutboxPublisherJob.cs
│   │   │   ├── EmailNotificationJob.cs
│   │   │   └── SmsNotificationJob.cs
│   │   ├── Messaging/
│   │   │   ├── RedisStreamConsumer.cs
│   │   │   ├── RedisStreamProducer.cs
│   │   │   ├── ServiceBusSubscriber.cs
│   │   │   └── SignalRNotificationService.cs
│   │   ├── Persistence/
│   │   │   ├── ApplicationDbContext.cs
│   │   │   └── Entities/
│   │   │       └── OutboxMessage.cs
│   │   ├── Presence/
│   │   │   ├── RedisPresenceTracker.cs
│   │   │   └── RedisGroupMembership.cs
│   │   └── DependencyInjection.cs
│   │
│   └── NotificationEngine.Domain/
│       ├── IAggregateRoot.cs
│       ├── DomainEventBase.cs
│       └── IDomainEvent.cs
│
├── docker-compose.yml                    # Infrastructure containers
├── test-signalr.html                     # SignalR test page
├── README.md                             # Project documentation
└── PROJECT_DECISIONS.md                  # This file
```

---

## Docker Infrastructure

```yaml
# docker-compose.yml
services:
  sql:
    image: mcr.microsoft.com/mssql/server:2022-latest
    ports: ["1433:1433"]
    environment:
      SA_PASSWORD: "YourStrong@Passw0rd"
      ACCEPT_EULA: "Y"

  redis:
    image: redis:7-alpine
    ports: ["6379:6379"]

  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports: ["10000-10002:10000-10002"]
```

---

## Future Considerations

1. **Production JWT:** Replace symmetric key with asymmetric (RSA/ECDSA) and integrate with real IdentityProvider
2. **Database Migrations:** Use EF Core migrations instead of manual table creation
3. **SignalR Auth:** Re-enable proper authentication for production
4. **Monitoring:** Add Prometheus metrics via OpenTelemetry
5. **Scaling:** Redis backplane already supports multiple API instances
6. **Testing:** Add integration tests for SignalR hub and presence

---

## Commands Reference

### Start Infrastructure
```bash
docker-compose up -d
```

### Run API
```bash
dotnet run --project src/NotificationEngine.Api
```

### Generate Test Token
```bash
curl "http://localhost:5270/dev/token?userId=test-user-1&tenantId=test-tenant-1"
```

### Test SignalR Negotiate
```bash
curl -X POST -H "Authorization: Bearer <token>" \
  "http://localhost:5270/hubs/dashboard/negotiate?negotiateVersion=1"
```

### Check Redis Presence
```bash
docker exec notification-engine-redis redis-cli ZRANGE presence:users 0 -1 WITHSCORES
```

### Check Hangfire Dashboard
```
http://localhost:5270/hangfire
```

---

*Document generated: 2026-03-29*
*Project: Notification Engine*
*Framework: .NET 8*
