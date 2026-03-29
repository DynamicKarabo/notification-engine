# ADR-001: Use Redis Streams as the Event Backbone

**Date:** 2024-03-01  
**Status:** Accepted  
**Deciders:** Platform Team  

---

## Context

We need a durable, ordered event backbone that allows multiple internal producers to write events and multiple consumer groups to process them independently without interfering with each other. The backbone must survive consumer crashes and support replay for new consumers or after failures.

Candidates evaluated:

| Option | Ordered | Consumer Groups | Persistence | Operational Cost |
|---|---|---|---|---|
| Redis Pub/Sub | No | No | No | Low |
| Redis Streams | Yes | Yes | Yes (up to MaxLen) | Low |
| Kafka | Yes | Yes | Yes (configurable) | High |
| RabbitMQ | Partially | Via exchanges | Optional | Medium |
| Azure Service Bus | No (per topic) | Yes (subscriptions) | Yes | Medium |

## Decision

Use **Redis Streams** as the primary internal event backbone.

## Rationale

- **Already in the stack.** Redis is required for the SignalR backplane regardless. Adding Streams costs no additional operational surface.
- **Consumer groups with exactly-once semantics.** XREADGROUP with explicit XACK provides the guarantees we need without a separate broker.
- **Low latency.** Sub-millisecond enqueue/dequeue versus Kafka's higher baseline latency (network + broker replication).
- **Sufficient durability.** With Redis persistence (AOF + RDB) and the Azure Cache for Redis Enterprise tier, stream data survives restarts and single-node failures.
- **Simpler ops.** Kafka requires Zookeeper/KRaft, partition management, and topic replication configuration. Redis Streams has none of this overhead at our scale.

## Trade-offs

- **Not infinitely durable.** Streams are bounded by `MAXLEN`. Messages beyond the retention window are lost. Mitigation: the outbox pattern persists all domain events to SQL before writing to the stream. The stream is a delivery mechanism, not the system of record.
- **No schema registry.** Kafka has Confluent Schema Registry. We compensate with JSON Schema validation on the consumer side and versioned event contracts.
- **Single-region.** Redis Streams do not natively replicate across regions. Accepted — see ADR-005 for outbox pattern which bridges this gap.

## Consequences

- All producers must use `IEventProducer` (not raw Redis commands) to ensure stream naming conventions and metadata fields are applied consistently.
- Consumers must implement pending entry list (PEL) recovery to handle process crashes.
- Stream retention is set to 1,000,000 entries with approximate trimming. This is reviewed quarterly.
