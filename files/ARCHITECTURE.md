# Architecture — Real-Time Notification & Live Dashboard Engine

## Table of Contents

1. [System Overview](#1-system-overview)
2. [High-Level Architecture](#2-high-level-architecture)
3. [Component Design](#3-component-design)
   - 3.1 [SignalR Hub Layer](#31-signalr-hub-layer)
   - 3.2 [Redis Streams Event Backbone](#32-redis-streams-event-backbone)
   - 3.3 [MediatR Application Pipeline](#33-mediatr-application-pipeline)
   - 3.4 [Hangfire Job Orchestration](#34-hangfire-job-orchestration)
   - 3.5 [Azure Service Bus Integration](#35-azure-service-bus-integration)
4. [Data Flows](#4-data-flows)
5. [Scaling Model](#5-scaling-model)
6. [Resilience & Fault Tolerance](#6-resilience--fault-tolerance)
7. [Security Model](#7-security-model)
8. [Observability](#8-observability)

---

## 1. System Overview

The engine solves two distinct but related problems:

- **Live Dashboards** — push incremental state updates to browser and mobile clients with sub-second latency as underlying data changes.
- **Instant Notifications** — deliver targeted, typed notifications to specific users, roles, or broadcast groups with guaranteed delivery and optional persistence.

The design separates the **event ingestion path** (where events enter the system) from the **delivery path** (where events reach clients). This decoupling allows each path to scale independently and absorb backpressure without cascading failures.

```
External Systems / Internal Services
          │
          ▼
  ┌───────────────┐      ┌────────────────────┐
  │  Azure Service│─────▶│  Stream Producer   │
  │  Bus Topics   │      │  (Redis Streams)   │
  └───────────────┘      └────────────────────┘
                                   │
                         ┌─────────▼──────────┐
                         │  Redis Stream       │
                         │  (ordered log)      │
                         └─────────┬──────────┘
                                   │
                    ┌──────────────▼──────────────┐
                    │   Consumer Group Workers     │
                    │   (Background Services)      │
                    └──────────────┬──────────────┘
                                   │
                         ┌─────────▼──────────┐
                         │  MediatR Pipeline   │
                         │  (domain events)    │
                         └─────────┬──────────┘
                                   │
               ┌───────────────────┼───────────────────┐
               │                   │                   │
       ┌───────▼──────┐  ┌────────▼───────┐  ┌───────▼──────┐
       │  SignalR Hub │  │ Persist to DB  │  │ Hangfire Job │
       │  Broadcast   │  │ (Outbox flush) │  │ (email/SMS)  │
       └───────┬──────┘  └────────────────┘  └──────────────┘
               │
       ┌───────▼──────────────────────────────────┐
       │          Connected Clients                │
       │  (Browser WebSocket / Mobile SSE)         │
       └──────────────────────────────────────────┘
```

---

## 2. High-Level Architecture

### Architectural Style

The system follows **Clean Architecture** with a layered decomposition:

```
┌──────────────────────────────────────────────────┐
│                   API Layer                       │
│  ASP.NET Core · SignalR Hubs · Minimal API        │
├──────────────────────────────────────────────────┤
│               Application Layer                   │
│  MediatR · Commands · Queries · Domain Events     │
├──────────────────────────────────────────────────┤
│                 Domain Layer                      │
│  Aggregates · Value Objects · Domain Events       │
├──────────────────────────────────────────────────┤
│             Infrastructure Layer                  │
│  Redis · Service Bus · Hangfire · EF Core         │
└──────────────────────────────────────────────────┘
```

### Deployment Topology

```
                    ┌─────────────────────────────┐
                    │       Azure Load Balancer    │
                    │    (WebSocket-aware / ARR)   │
                    └──────┬──────────┬────────────┘
                           │          │
               ┌───────────▼──┐  ┌───▼──────────┐
               │  API Pod 1   │  │  API Pod 2   │  ... (N pods)
               │  .NET 8      │  │  .NET 8      │
               │  SignalR Hub │  │  SignalR Hub │
               └──────┬───────┘  └──────┬───────┘
                      │                 │
               ┌──────▼─────────────────▼──────┐
               │      Redis Cluster 7.2          │
               │  ┌──────────┐  ┌────────────┐  │
               │  │ Streams  │  │ Pub/Sub    │  │
               │  │ (events) │  │ (backplane)│  │
               │  └──────────┘  └────────────┘  │
               └───────────────────────────────┘
                      │
               ┌──────▼────────────────┐
               │  Azure Service Bus    │
               │  (cross-service fan)  │
               └──────────────────────┘
                      │
               ┌──────▼────────────────┐
               │  SQL Server / Azure   │
               │  SQL (Hangfire +      │
               │  Application DB)      │
               └──────────────────────┘
```

---

## 3. Component Design

### 3.1 SignalR Hub Layer

#### Hub Design

The primary hub (`DashboardHub`) is a typed SignalR hub using a shared `IDashboardClient` interface, which enables compile-time safety for all client method calls.

```csharp
// Contracts/IDashboardClient.cs
public interface IDashboardClient
{
    Task ReceiveNotification(NotificationPayload notification);
    Task ReceiveDashboardUpdate(DashboardUpdatePayload update);
    Task ReceivePresenceUpdate(PresencePayload presence);
    Task ReceiveSystemAlert(SystemAlertPayload alert);
}

// Api/Hubs/DashboardHub.cs
[Authorize]
public class DashboardHub : Hub<IDashboardClient>
{
    private readonly IPresenceTracker _presenceTracker;
    private readonly IGroupMembership _groupMembership;
    private readonly ILogger<DashboardHub> _logger;

    public DashboardHub(
        IPresenceTracker presenceTracker,
        IGroupMembership groupMembership,
        ILogger<DashboardHub> logger)
    {
        _presenceTracker = presenceTracker;
        _groupMembership = groupMembership;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier!;
        var connectionId = Context.ConnectionId;

        // Register presence
        await _presenceTracker.UserConnectedAsync(userId, connectionId);

        // Join user-specific group
        await Groups.AddToGroupAsync(connectionId, $"user:{userId}");

        // Join tenant/role groups based on claims
        var tenantId = Context.User!.FindFirstValue("tenant_id");
        if (tenantId is not null)
            await Groups.AddToGroupAsync(connectionId, $"tenant:{tenantId}");

        // Restore any missed notifications (replay from Redis)
        await SendMissedNotificationsAsync(userId);

        await base.OnConnectedAsync();

        _logger.LogInformation(
            "Client connected. UserId={UserId} ConnectionId={ConnectionId}",
            userId, connectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier!;
        await _presenceTracker.UserDisconnectedAsync(userId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task AcknowledgeNotification(string notificationId)
    {
        // Client explicitly acks — update persistence
        await _mediator.Send(new AcknowledgeNotificationCommand(
            notificationId,
            Context.UserIdentifier!));
    }

    public async Task SubscribeToDashboard(string dashboardId)
    {
        // Runtime group subscription for dashboard-scoped updates
        if (await _groupMembership.CanAccessAsync(Context.UserIdentifier!, dashboardId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"dashboard:{dashboardId}");
    }
}
```

#### Presence Tracking

Presence is stored in Redis with a TTL-based heartbeat pattern. Each connected user has a sorted set entry (`presence:users`) scored by last-seen Unix timestamp.

```csharp
public class RedisPresenceTracker : IPresenceTracker
{
    private const string PresenceKey = "presence:users";
    private const string ConnectionsKeyPrefix = "presence:connections:";

    public async Task UserConnectedAsync(string userId, string connectionId)
    {
        var db = _redis.GetDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await db.SortedSetAddAsync(PresenceKey, userId, now);
        await db.SetAddAsync($"{ConnectionsKeyPrefix}{userId}", connectionId);
    }

    public async Task<IEnumerable<string>> GetOnlineUsersAsync()
    {
        // Users active in last 30 seconds
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds();
        var db = _redis.GetDatabase();
        return (await db.SortedSetRangeByScoreAsync(PresenceKey, cutoff, double.PositiveInfinity))
            .Select(v => v.ToString());
    }
}
```

#### Redis Backplane

With multiple API pods, the SignalR Redis backplane synchronises hub method calls across all instances. Any pod can broadcast to any group and the backplane propagates it to the pod(s) that hold the matching connections.

```csharp
// Program.cs
builder.Services.AddSignalR()
    .AddStackExchangeRedis(connectionString, options =>
    {
        options.Configuration.ChannelPrefix = RedisChannel.Literal("signalr");
        options.Configuration.ConnectRetry = 5;
        options.Configuration.ReconnectRetryPolicy = new LinearRetry(500);
    });
```

---

### 3.2 Redis Streams Event Backbone

Redis Streams provides an append-only, ordered log with consumer group semantics. This is the backbone that decouples event producers from the SignalR delivery layer.

#### Stream Naming Convention

```
stream:notifications:{tenantId}    # Tenant-scoped notification events
stream:dashboard:{dashboardId}     # Dashboard-scoped update events
stream:system                      # Platform-wide system events
stream:dlq                         # Dead-letter queue
```

#### Producer

```csharp
public class RedisStreamProducer : IEventProducer
{
    private readonly IConnectionMultiplexer _redis;

    public async Task<string> ProduceAsync<TEvent>(
        string streamKey,
        TEvent @event,
        CancellationToken ct = default)
        where TEvent : IDomainEvent
    {
        var db = _redis.GetDatabase();

        var entries = new NameValueEntry[]
        {
            new("type",    @event.GetType().Name),
            new("payload", JsonSerializer.Serialize(@event, _jsonOptions)),
            new("version", @event.Version.ToString()),
            new("trace_id", Activity.Current?.TraceId.ToString() ?? string.Empty),
        };

        // XADD with MAXLEN ~1_000_000 (approximate trimming)
        var messageId = await db.StreamAddAsync(
            streamKey,
            entries,
            maxLength: 1_000_000,
            useApproximateMaxLength: true);

        return messageId.ToString();
    }
}
```

#### Consumer (Background Service)

Each background service represents a named consumer group member. Multiple replicas of the same consumer group name allow parallel processing while Redis guarantees each message goes to exactly one consumer.

```csharp
public class NotificationStreamConsumer : BackgroundService
{
    private const string StreamKey   = "stream:notifications:*";
    private const string GroupName   = "notification-processor";
    private const string ConsumerName = ""; // Populated at startup from hostname

    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumerName = $"{GroupName}-{Environment.MachineName}";
        var db = _redis.GetDatabase();

        // Ensure consumer group exists (idempotent)
        await EnsureConsumerGroupAsync(db, StreamKey, GroupName);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Read up to 50 messages, block for 1s if empty
            var results = await db.StreamReadGroupAsync(
                StreamKey,
                GroupName,
                consumerName,
                position: ">",     // Only undelivered
                count: 50,
                noAck: false);

            if (results.Length == 0)
            {
                // Also process PEL (Pending Entry List) — messages
                // delivered but not yet acknowledged (crash recovery)
                await ProcessPendingAsync(db, consumerName, stoppingToken);
                await Task.Delay(100, stoppingToken);
                continue;
            }

            await ProcessBatchAsync(db, results, stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(
        IDatabase db,
        StreamEntry[] entries,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        foreach (var entry in entries)
        {
            try
            {
                var eventType = entry["type"].ToString();
                var payload   = entry["payload"].ToString();

                var domainEvent = DeserializeEvent(eventType, payload);
                await mediator.Publish(domainEvent, ct);

                // Explicit ACK — removes from PEL
                await db.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing entry {EntryId}", entry.Id);
                await HandleFailedEntryAsync(db, entry, ex);
            }
        }
    }

    private async Task ProcessPendingAsync(
        IDatabase db,
        string consumerName,
        CancellationToken ct)
    {
        // Claim messages idle for > 30s (previous consumer crashed)
        var pending = await db.StreamPendingMessagesAsync(
            StreamKey, GroupName, 100, consumerName,
            minIdleTimeInMs: 30_000);

        if (pending.Length == 0) return;

        var ids = pending.Select(p => p.MessageId).ToArray();
        await db.StreamClaimAsync(StreamKey, GroupName, consumerName, 30_000, ids);
    }
}
```

#### Dead-Letter Handling

Messages that fail processing after `MaxRetries` are written to `stream:dlq` with full diagnostic context. A separate Hangfire job polls the DLQ for manual review or automated replay.

---

### 3.3 MediatR Application Pipeline

MediatR serves two roles: **command/query dispatch** (CQRS) and **domain event fan-out** (notification pattern).

#### Pipeline Behaviours (ordered)

```
Request
  │
  ▼
LoggingBehaviour          ← Structured entry/exit log with timing
  │
  ▼
ValidationBehaviour       ← FluentValidation, throws on failure
  │
  ▼
AuthorisationBehaviour    ← Attribute-based resource checks
  │
  ▼
PerformanceBehaviour      ← Warn if handler > 500ms
  │
  ▼
TransactionBehaviour      ← Wraps commands in DB transaction
  │
  ▼
Handler                   ← Your command/query handler
  │
  ▼
Response
```

```csharp
// Application/Behaviours/TransactionBehaviour.cs
public class TransactionBehaviour<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICommand<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            var response = await next();
            await tx.CommitAsync(ct);
            return response;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
```

#### Domain Event Dispatch

Domain events are raised within aggregates and collected by the EF Core `SaveChanges` override. After the transaction commits, they are dispatched via MediatR's `Publish`.

```csharp
// Infrastructure/Persistence/ApplicationDbContext.cs
public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
{
    var domainEvents = ChangeTracker.Entries<IAggregateRoot>()
        .SelectMany(e => e.Entity.DomainEvents)
        .ToList();

    var result = await base.SaveChangesAsync(ct);

    // Dispatch after commit — outbox ensures at-least-once if process crashes here
    foreach (var @event in domainEvents)
        await _mediator.Publish(@event, ct);

    return result;
}
```

#### Domain Event Handlers

Each handler is a thin router that decides whether to push via SignalR, enqueue a Hangfire job, or both.

```csharp
public class OrderStatusChangedHandler
    : INotificationHandler<OrderStatusChangedEvent>
{
    private readonly IHubContext<DashboardHub, IDashboardClient> _hub;
    private readonly IBackgroundJobClient _jobs;

    public async Task Handle(OrderStatusChangedEvent @event, CancellationToken ct)
    {
        // 1. Real-time push to relevant dashboard group
        var payload = new DashboardUpdatePayload(
            EntityType: "Order",
            EntityId:   @event.OrderId,
            Data:       @event.NewStatus);

        await _hub.Clients
            .Group($"dashboard:{@event.DashboardId}")
            .ReceiveDashboardUpdate(payload);

        // 2. Persist notification for mobile clients (may be offline)
        _jobs.Enqueue<INotificationPersistenceJob>(
            j => j.PersistAsync(@event.UserId, payload, CancellationToken.None));

        // 3. Trigger downstream email if status is terminal
        if (@event.NewStatus.IsTerminal())
            _jobs.Enqueue<IEmailNotificationJob>(
                j => j.SendAsync(@event.UserId, @event.OrderId, CancellationToken.None));
    }
}
```

---

### 3.4 Hangfire Job Orchestration

Hangfire provides durable background job processing backed by SQL Server storage. It handles the reliable delivery of notifications that require side effects (email, SMS, webhook) and acts as the fallback path for offline mobile clients.

#### Job Types

| Job | Trigger | Retry Policy |
|---|---|---|
| `NotificationPersistenceJob` | Every domain event | 3× immediate, then 5× exp. backoff |
| `EmailNotificationJob` | Terminal state changes | 5× exp. backoff, max 24h |
| `SmsNotificationJob` | Critical alerts only | 3× exp. backoff, max 1h |
| `WebhookDeliveryJob` | Configured subscriptions | 10× exp. backoff, max 48h |
| `DlqReplayJob` | Scheduled (every 5 min) | Non-retrying (manual) |
| `PresenceCleanupJob` | Scheduled (every 60 s) | Non-retrying |

#### Retry Configuration

```csharp
// Infrastructure/Hangfire/RetryPolicies.cs
public static class RetryPolicies
{
    public static readonly AutomaticRetryAttribute Email =
        new()
        {
            Attempts = 5,
            DelaysInSeconds = new[] { 60, 300, 900, 3600, 86400 },
            OnAttemptsExceeded = AttemptsExceededAction.MoveToDeadLetter
        };

    public static readonly AutomaticRetryAttribute Webhook =
        new()
        {
            Attempts = 10,
            DelaysInSeconds = Enumerable
                .Range(1, 10)
                .Select(i => (int)Math.Pow(2, i) * 30)   // 60s, 120s, 240s …
                .ToArray(),
            OnAttemptsExceeded = AttemptsExceededAction.MoveToDeadLetter
        };
}
```

#### Dead-Letter Recovery

Dead-lettered Hangfire jobs appear in the Hangfire dashboard under "Failed" and can be re-queued manually or via the `DlqReplayJob`. The `DlqReplayJob` also checks `stream:dlq` in Redis for stream-level dead letters.

```csharp
[DisableConcurrentExecution(10)]
public class DlqReplayJob : IDlqReplayJob
{
    public async Task ReplayAsync(CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var entries = await db.StreamReadAsync("stream:dlq", "0-0", count: 100);

        foreach (var entry in entries)
        {
            var originalStream = entry["original_stream"].ToString();
            var payload        = entry["payload"].ToString();
            var attempts       = int.Parse(entry["attempts"].ToString());

            if (attempts >= MaxReplayAttempts)
            {
                _logger.LogWarning("Abandoning DLQ entry {Id} after {Attempts} attempts",
                    entry.Id, attempts);
                await db.StreamAcknowledgeAsync("stream:dlq", "dlq-group", entry.Id);
                continue;
            }

            // Re-publish to original stream
            await _producer.ProduceRawAsync(originalStream, payload, ct);
            await db.StreamAcknowledgeAsync("stream:dlq", "dlq-group", entry.Id);
        }
    }
}
```

---

### 3.5 Azure Service Bus Integration

Azure Service Bus handles cross-service communication where other internal services (Order Service, Inventory Service, etc.) need to publish events that this engine must react to.

#### Topic Structure

```
notifications/
  ├── subscriptions/
  │     └── notification-engine          ← This service's subscription
  │
dashboard-updates/
  ├── subscriptions/
  │     └── notification-engine
  │
system-alerts/
  ├── subscriptions/
  │     └── notification-engine
```

#### Subscriber

```csharp
public class ServiceBusSubscriber : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = _serviceBusClient.CreateProcessor(
            topicName:        "notifications",
            subscriptionName: "notification-engine",
            new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = 16,
                AutoCompleteMessages = false,
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5)
            });

        processor.ProcessMessageAsync += HandleMessageAsync;
        processor.ProcessErrorAsync   += HandleErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
        await processor.StopProcessingAsync();
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        using var activity = _tracer.StartActivity("ServiceBusMessage.Process");
        activity?.SetTag("message.id", args.Message.MessageId);

        try
        {
            var eventType = args.Message.ApplicationProperties["eventType"].ToString();
            var @event    = DeserializeMessage(eventType, args.Message.Body);

            // Write to Redis Stream — normalise to internal event backbone
            await _producer.ProduceAsync($"stream:notifications:{@event.TenantId}", @event);
            await args.CompleteMessageAsync(args.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed processing Service Bus message {MessageId}",
                args.Message.MessageId);

            // Abandon → Service Bus retry / DLQ after max delivery count
            await args.AbandonMessageAsync(args.Message);
        }
    }
}
```

---

## 4. Data Flows

### Flow A — Real-Time Dashboard Update

```
1. External service publishes to Azure Service Bus topic "dashboard-updates"
2. ServiceBusSubscriber receives message, normalises to DashboardUpdateEvent
3. DashboardUpdateEvent written to Redis Stream "stream:dashboard:{dashboardId}"
4. NotificationStreamConsumer reads from consumer group, publishes via MediatR
5. DashboardUpdateHandler calls IHubContext.Clients.Group("dashboard:{id}").ReceiveDashboardUpdate()
6. SignalR backplane propagates to all pods
7. Pod holding the WebSocket connection sends frame to client browser
8. StreamAcknowledge confirms exactly-once processing
```

### Flow B — Targeted User Notification

```
1. Internal command issued: SendNotificationCommand { UserId, Type, Payload }
2. MediatR dispatches through pipeline (validation → authorisation → handler)
3. Handler:
   a. Persists notification to DB (via outbox)
   b. Checks presence tracker — is user online?
      → YES: IHubContext.Clients.User(userId).ReceiveNotification()
      → NO:  Enqueue Hangfire job for push/email fallback
4. EF Core SaveChanges → outbox entry flushed → Redis Stream entry created
5. Delivery confirmed by client ACK (AcknowledgeNotification hub method)
```

### Flow C — Broadcast Alert

```
1. POST /api/alerts { Severity, Message, Scope: "tenant:{id}" | "all" }
2. Minimal API endpoint sends BroadcastAlertCommand via MediatR
3. Handler determines target group
4. IHubContext.Clients.Group(targetGroup).ReceiveSystemAlert()
5. Simultaneously: Hangfire enqueues email job for offline users in group
```

---

## 5. Scaling Model

### Horizontal Scaling

| Layer | Strategy | Notes |
|---|---|---|
| API Pods | Stateless scale-out | Session affinity NOT required; Redis backplane handles it |
| Redis | Redis Cluster | Streams sharded by key; backplane on dedicated node |
| Hangfire Workers | Dedicated worker pods | Separate from API pods in production |
| Service Bus Namespace | Premium tier | Dedicated capacity units, 1-4 MU |

### Connection Limits

A single SignalR pod can sustain ~20,000 concurrent WebSocket connections on 2 vCPUs / 4 GB RAM. Scale pods horizontally; the Redis backplane ensures cross-pod delivery.

### Stream Partitioning

For very high-throughput tenants, the stream key includes a shard suffix:

```
stream:notifications:{tenantId}:{shardIndex}
```

The producer hashes `userId % shardCount` to assign a shard. Consumer group workers are bound to their assigned shards.

---

## 6. Resilience & Fault Tolerance

### Failure Modes and Mitigations

| Failure | Detection | Mitigation |
|---|---|---|
| Redis unavailable | HealthCheck → circuit breaker | Degrade to poll-based API; queue events in Service Bus |
| API pod crash mid-stream | PEL timeout (30s) | Sibling consumer claims and reprocesses pending entries |
| SignalR connection drop | Client `onclose` event | Auto-reconnect with exponential backoff + event replay |
| Hangfire SQL unavailable | Polly circuit breaker | In-memory queue (bounded, 10k limit); alert fired |
| Service Bus throttling | `ServiceBusException` 429 | Polly retry with jitter; auto-scaling rule triggers |

### Client Reconnection & Replay

On reconnect, the client sends its last received sequence ID. The hub replays any events stored in Redis with IDs newer than that sequence.

```csharp
public async Task ReplayFrom(string lastEventId)
{
    var userId = Context.UserIdentifier!;
    var db     = _redis.GetDatabase();

    // Read entries newer than lastEventId from user's notification stream
    var entries = await db.StreamRangeAsync(
        $"stream:notifications:user:{userId}",
        minId: lastEventId,
        maxId: "+",
        count: 200);

    foreach (var entry in entries)
    {
        var payload = JsonSerializer.Deserialize<NotificationPayload>(entry["payload"]!);
        await Clients.Caller.ReceiveNotification(payload!);
    }
}
```

---

## 7. Security Model

### Authentication

All SignalR hub connections require a valid JWT Bearer token. The token is passed as a query string parameter during the WebSocket upgrade (standard SignalR pattern).

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Extract token from query string for WebSocket negotiation
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!StringValues.IsNullOrEmpty(token) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
```

### Authorisation

Group membership is enforced before `AddToGroupAsync`. Users can only subscribe to dashboards they have access to, verified against the `IGroupMembership` service (backed by the application database).

### Message Validation

All inbound Service Bus messages are validated against a JSON Schema registry. Unknown or malformed schemas are rejected and moved to the Service Bus DLQ.

---

## 8. Observability

### Structured Logging (Serilog)

Every significant operation emits a structured log event with:
- `TraceId`, `SpanId` (propagated from OpenTelemetry)
- `UserId`, `TenantId` (from JWT claims scope)
- `StreamKey`, `MessageId` (for stream events)
- `ElapsedMs`, `Success`

### Metrics (System.Diagnostics.Metrics + Prometheus)

| Metric | Type | Labels |
|---|---|---|
| `notifications_dispatched_total` | Counter | `type`, `tenant` |
| `stream_consumer_lag` | Gauge | `stream`, `group` |
| `signalr_connections_active` | Gauge | `pod` |
| `hangfire_jobs_failed_total` | Counter | `job_type` |
| `notification_latency_ms` | Histogram | `type` |

### Distributed Tracing (OpenTelemetry)

Traces span the full path from Service Bus message receipt to SignalR frame delivery. The `TraceId` is propagated via Redis Stream entry metadata.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddRedisInstrumentation(_redis)
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource("NotificationEngine.*")
        .AddAzureMonitorTraceExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());
```

### Health Checks

```
GET /health         → Liveness (always 200 if process is alive)
GET /health/ready   → Readiness (Redis, SQL, Service Bus connectivity)
GET /health/live    → Liveness with component breakdown
```
