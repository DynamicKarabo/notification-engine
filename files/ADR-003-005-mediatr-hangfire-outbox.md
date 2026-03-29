# ADR-003: MediatR as the Application Pipeline and Domain Event Dispatcher

**Date:** 2024-03-08  
**Status:** Accepted  
**Deciders:** Platform Team  

---

## Context

We need a mechanism to:

1. Dispatch commands and queries from API controllers to their handlers without tight coupling.
2. Fan domain events out to multiple independent handlers (SignalR push, persistence, job enqueueing) without handlers knowing about each other.
3. Add cross-cutting concerns (validation, logging, tracing, transactions) in a composable way.

## Decision

Use **MediatR** (`MediatR` + `MediatR.Extensions.Microsoft.DependencyInjection`) for both CQRS dispatch and the notification pattern.

## Rationale

- **Single dispatch model.** Commands (`IRequest<T>`), queries (`IRequest<T>`), and domain events (`INotification`) all flow through the same pipeline, making the application layer consistent.
- **Pipeline behaviours.** Cross-cutting concerns are expressed as ordered `IPipelineBehavior<TRequest, TResponse>` implementations, registered once and applied everywhere. This is cleaner than decorators or interceptors.
- **No runtime magic.** MediatR is compile-time resolvable through DI. No reflection on the hot path (after startup).
- **Domain event fan-out.** `INotificationHandler<TEvent>` allows multiple handlers to respond to the same domain event. Handlers are isolated and independently testable.

## Trade-offs

- **Not truly async fan-out.** MediatR's `Publish` dispatches to handlers sequentially by default (or in parallel with `PublishStrategy`). For high-volume events, this means slow handlers block fast ones. Mitigation: long-running work (email, SMS) is always delegated to Hangfire inside the handler, keeping handler execution time < 5ms.
- **In-process only.** MediatR does not cross process boundaries. Cross-service events go through Azure Service Bus → Redis Streams first, then MediatR. This is intentional — MediatR is not a message bus.
- **Adds abstraction.** Some developers find MediatR adds indirection. This is accepted as the trade-off for a uniform, pipeable application model.

## Pipeline Behaviour Order

```
LoggingBehaviour → ValidationBehaviour → AuthorisationBehaviour → PerformanceBehaviour → TransactionBehaviour → Handler
```

Behaviours are registered in this order in `DependencyInjection.cs`. Ordering is enforced by test.

## Consequences

- All commands must implement `ICommand<TResponse>` (a marker interface extending `IRequest<TResponse>`). This ensures `TransactionBehaviour` only applies to state-mutating operations.
- Domain events raised in aggregates are collected by `ApplicationDbContext.SaveChangesAsync` and published post-commit.
- MediatR's `PublishStrategy` is set to `SyncContinueOnException` for domain events so a failing handler does not block others.

---

# ADR-004: Hangfire for Background Job Orchestration (vs MassTransit Sagas)

**Date:** 2024-03-10  
**Status:** Accepted  
**Deciders:** Platform Team  

---

## Context

We need reliable background job execution for notification side-effects (email, SMS, webhooks) and recurring maintenance tasks (DLQ replay, presence cleanup). Requirements:

- At-least-once delivery with configurable retry policies
- Dead-letter handling for failed jobs
- Operational visibility (dashboard)
- Simple integration with the existing .NET DI container

Candidates evaluated:

| Option | Complexity | Dashboard | Sagas | DB Storage |
|---|---|---|---|---|
| Hangfire | Low | Built-in | No | SQL Server |
| MassTransit + Quartz | High | Partial | Yes | SQL Server / Redis |
| Quartz.NET alone | Medium | No | No | SQL Server |
| Azure Functions | Medium | Azure Portal | No | Storage Account |

## Decision

Use **Hangfire** for background job processing.

## Rationale

- **Simplicity.** Hangfire's API (`BackgroundJob.Enqueue`, `RecurringJob.AddOrUpdate`) is immediately understandable. MassTransit Sagas add significant learning curve and XML-style configuration.
- **Built-in dashboard.** The Hangfire dashboard (`/hangfire`) provides real-time visibility into queued, processing, succeeded, and failed jobs without additional tooling.
- **SQL Server storage.** Hangfire uses the same SQL Server instance already in the stack. No additional storage tier required.
- **No saga requirement.** Our background workflows are simple fan-out jobs (enqueue one, process independently), not multi-step sagas with compensating transactions. MassTransit's saga machinery is overkill.
- **Proven in .NET ecosystem.** Hangfire has an extensive track record at scale.

## Trade-offs

- **No built-in saga/orchestration.** If we need multi-step durable workflows in the future (e.g., multi-stage approval flows), we will re-evaluate MassTransit or Azure Durable Functions.
- **SQL Server dependency.** Hangfire storage requires SQL Server uptime. Mitigation: Polly circuit breaker around Hangfire enqueue calls; in-memory bounded queue (10,000 limit) as temporary buffer.
- **Hangfire Pro license required** for advanced features (batches, continuations). Current scope does not require these.

## Queue Priority Configuration

```
critical → real-time notification delivery, DLQ recovery
default  → email notifications, persistence jobs
low      → analytics exports, cleanup tasks
```

Workers are configured with `Queues = ["critical", "default", "low"]` and process queues in order.

## Consequences

- Hangfire workers run in a separate Kubernetes Deployment (`notification-engine-worker`) to prevent job CPU contention with SignalR connection handling.
- All job interfaces follow the convention `IXxxJob` with a single `ExecuteAsync(CancellationToken)` method. This ensures jobs are testable without Hangfire internals.
- Dead-lettered jobs are retained for 30 days and trigger a PagerDuty alert if the dead-letter count exceeds 100.

---

# ADR-005: Outbox Pattern for Reliable Domain Event Publishing

**Date:** 2024-03-15  
**Status:** Accepted  
**Deciders:** Platform Team  

---

## Context

Domain events are raised inside aggregates and must be reliably delivered to downstream consumers (Redis Streams, Hangfire). The naive approach — raise an event in memory, dispatch via MediatR, and publish to Redis — creates a dual-write problem:

```
DB write succeeds → Redis publish fails → Event lost
DB write fails    → Redis publish succeeds → Phantom event
```

We need to guarantee that events are published **if and only if** the corresponding database transaction commits.

## Decision

Implement the **Transactional Outbox Pattern**.

## Implementation

1. An `OutboxMessage` table lives in the same SQL database as the application data.
2. Within the EF Core `SaveChangesAsync`, domain events are serialised and written to `OutboxMessage` **in the same transaction** as the business data.
3. A Hangfire recurring job (`OutboxPublisherJob`, every 5 seconds) reads unpublished outbox messages, publishes them to Redis Streams, and marks them as processed.
4. `ProcessedAt` timestamp + idempotency key on the consumer side ensures at-most-once delivery at the consumer.

```sql
CREATE TABLE OutboxMessage (
    Id              UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID() PRIMARY KEY,
    OccurredOn      DATETIME2        NOT NULL,
    Type            NVARCHAR(256)    NOT NULL,
    Payload         NVARCHAR(MAX)    NOT NULL,
    ProcessedAt     DATETIME2        NULL,
    Error           NVARCHAR(MAX)    NULL,
    IdempotencyKey  NVARCHAR(128)    NOT NULL,
    INDEX IX_OutboxMessage_ProcessedAt (ProcessedAt) INCLUDE (Id)
);
```

## Trade-offs

- **Polling delay.** The outbox publisher runs every 5 seconds by default. For truly real-time requirements (P99 < 100ms), the direct MediatR dispatch path (pre-commit) is used **in addition** to the outbox, with the outbox serving as the guaranteed fallback.
- **Additional table.** The outbox table grows proportionally to event volume. Processed messages older than 7 days are purged by a maintenance job.
- **Slightly more complex save path.** `ApplicationDbContext.SaveChangesAsync` now has outbox serialisation logic. This is encapsulated behind `IAggregateRoot` and not visible to handlers.

## Consequences

- The `OutboxPublisherJob` is marked `[DisableConcurrentExecution]` to prevent parallel publishing of the same outbox window.
- Consumer-side idempotency is enforced by checking `IdempotencyKey` against a Redis set before processing. The set entry TTL is 24 hours.
- The outbox table is the system of record for all domain events. Redis Streams is a delivery cache.
