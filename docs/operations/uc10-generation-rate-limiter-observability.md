# UC-10 Generation Rate Limiter Observability Guide

## 1. Overview

The UC-10 Generation Rate Limiter protects itinerary generation from abuse and server overload. In production, it operates in distributed mode using Redis to enforce an atomic rolling 60-second quota (default: 3 requests/minute) and a cooldown (default: 15 seconds) across multiple API instances. In single-instance deployments, it uses an in-memory bounded cache with idle TTL and automatic sweeps.

All rate limit decisions are tracked via `System.Diagnostics.Metrics` using the dedicated meter `TripMate.RateLimiting`.

---

## 2. Meter & Metric Definitions

- **Meter Name:** `TripMate.RateLimiting`

| Metric Name | Type | Unit | Description | Dimensions (Low Cardinality) |
|---|---|---|---|---|
| `tripmate.rate_limit.acquisitions` | Counter | `{requests}` | Total count of rate limit checks performed. | `provider`: `redis`, `single_instance`<br>`outcome`: `accepted`, `cooldown`, `quota`, `store_failure` |
| `tripmate.rate_limit.operation_duration_ms` | Histogram | `ms` | Latency of the store operation (Redis script execution or single-instance check). | `provider`: `redis`, `single_instance`<br>`outcome`: `success`, `failure` |
| `tripmate.rate_limit.active_users` | Observable Gauge | `{users}` | Number of active user keys tracked in the store/registry. | `provider`: `redis`, `single_instance` |
| `tripmate.rate_limit.evictions` | Counter | `{entries}` | Count of expired or idle entries pruned from state. | `provider`: `redis`, `single_instance` |

### Privacy and High-Cardinality Controls
- **User IDs** (`travelerUserId`), Idempotency Keys, Redis cluster endpoints, error messages, and raw request payloads are **strictly prohibited** from metric dimensions.
- Store failure logs are structured, omit connection credentials, and are throttled to a maximum frequency of once every 5 seconds per process to prevent log storms during store outages.

---

## 3. Recommended Prometheus & Grafana Dashboard Panels

### Panel 1: Rate Limiter Outcomes (Requests / sec)
Visualizes traffic composition: allowed generations vs. cooldown blocks vs. quota rejections vs. store failures.
```promql
sum by (outcome) (rate(tripmate_rate_limit_acquisitions_total[1m]))
```

### Panel 2: Store Latency (p50, p95, p99)
Monitors the execution duration of the atomic Lua script in Redis.
```promql
histogram_quantile(0.95, sum by (le) (rate(tripmate_rate_limit_operation_duration_ms_bucket{provider="redis"}[5m])))
histogram_quantile(0.99, sum by (le) (rate(tripmate_rate_limit_operation_duration_ms_bucket{provider="redis"}[5m])))
```

### Panel 3: Active User Cardinality
Tracks the active user pool in Redis registry or local memory. Redis reports the most recent
request-derived snapshot and automatically falls back to zero after its maximum validity period;
it is not a continuous registry poll. Across API instances, use `max` rather than `sum` because
each instance observes the same shared registry at potentially different times.
```promql
max(tripmate_rate_limit_active_users{provider="redis"})
```

### Panel 4: Eviction Rate
Tracks the cadence of TTL sweeps and expired entry pruning.
```promql
rate(tripmate_rate_limit_evictions_total[5m])
```

---

## 4. Operational Alerts & Runbook

The conditions below are starting templates, not approved production thresholds. Each deployment
must tune and record its thresholds from the two-instance smoke/load evidence before rollout.

### Alert: RateLimiterStoreFailureHigh
- **Condition:** `sum(rate(tripmate_rate_limit_acquisitions_total{outcome="store_failure"}[2m])) / sum(rate(tripmate_rate_limit_acquisitions_total[2m])) > 0.05`
- **Severity:** Critical
- **Impact:** Travelers receiving HTTP 503 `planning.generation_rate_limiter_unavailable`. Generations fail closed to protect downstream routing and AI providers.
- **Action:**
  1. Check Redis cluster connectivity and container status (`docker ps` / `kubectl get pods -l app=redis`).
  2. Verify network latency, CPU, and memory utilization on the Redis node.
  3. Inspect API server logs for Redis connection/timeout warnings.
  4. Once Redis connectivity is restored, the rate limiter recovers automatically without requiring an API restart.

### Alert: SingleInstanceCapacityExhaustion
- **Condition:** `tripmate_rate_limit_active_users{provider="single_instance"} >= 9500` (assuming `MaxTrackedUsers = 10000`)
- **Severity:** Warning
- **Impact:** New users may be rejected with 503 if idle TTL sweep cannot free space fast enough.
- **Action:**
  1. Check traffic patterns for bot attacks creating unique user IDs.
  2. In multi-instance environments, ensure `Provider` is set to `Redis` rather than `SingleInstance`.
