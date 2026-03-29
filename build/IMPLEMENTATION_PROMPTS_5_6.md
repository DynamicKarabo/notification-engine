# Implementation Guide - Prompt 5 & 6

## Prompt 5: Redis Streams Event Backbone & Azure Service Bus Integration

### Changes Made

**Infrastructure Layer:**

1. **RedisStreamProducer.cs** - Fixed API compatibility:
   - Changed `maxLength: MaxStreamLength` to `maxLength: (int)MaxStreamLength`

2. **RedisStreamConsumer.cs** - Background service for consuming Redis Streams:
   - Uses `XREADGROUP` with explicit `XACK` for exactly-once processing
   - Consumer group: `notification-processor`
   - Batch size: 50 messages
   - Dead-letter queue: `stream:dlq` for failed messages
   - Fixed API calls: removed `server.EndPoints`, `KeyExecute`, `minIdleTimeInMs`, `NameValueEntry.IsNull`

3. **ServiceBusSubscriber.cs** - Azure Service Bus integration:
   - Subscribes to topics: `notifications`, `dashboard-updates`, `system-alerts`
   - Routes external events to internal Redis Streams
   - Fixed API: `HandleErrorAsync` returns `Task`, uses `ErrorSource` instead of `Operation`

4. **SignalRNotificationService.cs** - Refactored to avoid circular dependency:
   - Uses non-generic `IHubContext<Hub>` with dynamic client invocation
   - Avoids referencing API types directly (DashboardHub, IDashboardClient)

---

## Prompt 6: Background Jobs with Hangfire

### Changes Made

**Application Layer - Job Interfaces:**

```csharp
// IEmailNotificationJob.cs
public interface IEmailNotificationJob
{
    Task ExecuteAsync(CancellationToken ct = default);
}

// ISmsNotificationJob.cs
public interface ISmsNotificationJob
{
    Task ExecuteAsync(CancellationToken ct = default);
}

// IOutboxPublisherJob.cs
public interface IOutboxPublisherJob
{
    Task ExecuteAsync(CancellationToken ct = default);
}
```

**Infrastructure Layer - Job Implementations:**

1. **EmailNotificationJob.cs** - Email notification processor
2. **SmsNotificationJob.cs** - SMS notification processor
3. **OutboxPublisherJob.cs** - Reads unpublished outbox messages, publishes to Redis Streams:
   - Decorated with `[DisableConcurrentExecution(60)]`
   - Processes up to 100 messages per run
   - Routes events based on type to appropriate streams

**Infrastructure/DependencyInjection.cs - Hangfire Configuration:**

```csharp
services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(sqlConnectionString, new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.FromSeconds(15),
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true
    }));

services.AddHangfireServer(options =>
{
    options.Queues = new[] { "critical", "default", "low" };
    options.WorkerCount = Environment.ProcessorCount * 5;
});
```

**Program.cs - Recurring Job Setup:**

```csharp
recurringJobManager.AddOrUpdate(
    "outbox-publisher",
    () => outboxJob.ExecuteAsync(default),
    "*/5 * * * *",  // Every 5 seconds
    new RecurringJobOptions
    {
        TimeZone = TimeZoneInfo.Utc,
        MisfireHandling = MisfireHandlingMode.Relaxed
    });
```

**API Layer - Dashboard:**

- Hangfire Dashboard at `/hangfire`
- Authorization filter requiring Admin role

---

## Package Updates

### Infrastructure.csproj
- Added: `Hangfire.AspNetCore` 1.8.6
- Added: `Hangfire.Core` 1.8.6
- Added: `Hangfire.SqlServer` 1.8.6
- Added: `Microsoft.AspNetCore.SignalR` 1.1.0
- Added: `Microsoft.Extensions.Hosting.Abstractions` 8.0.0

### Api.csproj
- Added: `Hangfire.AspNetCore` 1.8.6
- Added: `Hangfire.Core` 1.8.6
- Added: `Hangfire.SqlServer` 1.8.6

---

## Summary

| Prompt | Status | Key Components |
|--------|--------|----------------|
| 5 | ✅ Complete | RedisStreamProducer, RedisStreamConsumer, ServiceBusSubscriber, SignalRNotificationService (refactored) |
| 6 | ✅ Complete | Hangfire + SQL Server storage, OutboxPublisherJob (every 5s), EmailNotificationJob, SmsNotificationJob |
