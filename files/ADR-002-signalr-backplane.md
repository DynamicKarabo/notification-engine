# ADR-002: Redis Backplane for SignalR Scale-Out

**Date:** 2024-03-05  
**Status:** Accepted  
**Deciders:** Platform Team  

---

## Context

ASP.NET Core SignalR hub connections are affined to a single server process. When running multiple API pods, a message broadcast from pod A cannot reach clients connected to pod B without a coordination layer.

Candidates evaluated:

| Option | Complexity | Latency | Cost | .NET Support |
|---|---|---|---|---|
| Redis Backplane | Low | ~1ms | Included (reuses Redis) | First-party |
| Azure SignalR Service | Very Low | ~5ms | Per-connection billing | First-party |
| Custom Pub/Sub | High | Variable | None | Manual |

## Decision

Use the **Redis backplane** (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`).

## Rationale

- **No per-connection billing.** Azure SignalR Service charges per concurrent connection per unit. At 100k connections, this is a significant cost.
- **Lower latency.** Redis Pub/Sub operates within the same VNET with sub-millisecond delivery. Azure SignalR Service adds an external hop.
- **Simpler data gravity.** The Redis instance is already present for Streams and presence tracking. Adding the backplane is a one-liner (`AddStackExchangeRedis`).
- **Full control.** We can monitor and alert on backplane channel depth via Redis metrics.

## Trade-offs

- **Redis is now a critical dependency.** If Redis is unavailable, SignalR scale-out fails. Mitigation: Redis Enterprise with zone-redundancy and AOF persistence. Circuit breaker degrades to single-pod mode with a warning in logs.
- **Backplane channel saturation.** Very high broadcast rates can saturate the Pub/Sub channel. At our current projections (< 1,000 broadcasts/sec) this is not a concern. If we exceed 10,000 broadcasts/sec, we will evaluate sharded channels or moving to Azure SignalR Service.

## Consequences

- Dedicated Redis logical database (DB 1) for backplane channels to isolate from Streams (DB 0).
- Channel prefix set to `signalr:` to prevent collision with other Pub/Sub consumers.
- Monitoring: `redis_pubsub_channels` and `redis_pubsub_messages_published_total` metrics added to Grafana dashboard.
