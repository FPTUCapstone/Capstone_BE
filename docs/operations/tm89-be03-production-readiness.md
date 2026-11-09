# TM-89 BE-03 production-readiness checklist

This checklist is an activation gate, not authorization to deploy, migrate, merge, or change Railway.
PC-05 production acceptance must not be claimed until every required item is evidenced and approved.

## Merge/deployment coupling

- [x] `CheckInAuditJournal:Enabled=false` is the repository default and deliberately makes UC-43
  check-in fail closed with MSG127 before verification or ticket mutation. It does not preserve the
  legacy check-in path.
- [x] Railway production evidence inspected 2026-11-09: service `Capstone_BE` is connected to branch
  `develop` with **Auto deploys when pushed to GitHub** enabled.
- [ ] An owner-approved activation/rollout plan prevents a merge to `develop` from deploying a binary
  that blocks all check-ins. Until then, **MERGE BLOCKED** even if code review and CI pass.

## Redis durability and capacity

- [ ] Production Redis uses persistent storage with `appendonly yes`, approved `appendfsync`, and a
  verified restart/recovery test on the actual volume.
- [ ] Production `maxmemory`/eviction behavior is verified safe for immutable audit streams and receipt
  hashes; `noeviction` or any alternative requires infrastructure-owner approval.
- [ ] Capacity evidence covers shared Redis workload, unbounded streams/receipts, SQL outage backlog,
  and the measured append + WAITAOF p50/p95/p99. No retention value is inferred here.
- [ ] Backup, restore, corruption recovery, access control, credential rotation, and ownership are
  documented and tested without exposing secrets.

## Projection and poison handling

- [x] Pending recovery is bounded, advances an XAUTOCLAIM cursor, and reserves processing capacity for
  fresh events on every sweep.
- [x] Malformed/conflicting events remain pending: no ACK, XDEL, XTRIM, expiry, or silent discard.
- [x] ACK=0 is recorded separately and is not reported as a successfully processed stream entry.
- [ ] Production monitoring exposes journal availability, append/WAITAOF latency, stream depth and
  oldest age, pending depth, poison/conflicts, ACK misses, projection retry/failure, and admission-only age.
- [ ] Operator runbook defines investigation and approved resolution of poison/conflicting entries.

## SQL and rollout

- [ ] Database-first migration is backed up, rehearsed, reviewed, and applied before enabling/deploying
  code that projects `journal_event_id` and `scan_attempt_id`.
- [ ] Rollback order is approved and tested. It must not re-enable the legacy best-effort SQL fallback
  or discard admitted Redis evidence.
- [ ] Backend health/smoke checks cover admission failure, known rejection, successful check-in,
  SQL outage/recovery, duplicate/conflict, and Redis restart.

## Product-owner policies still PENDING

- [ ] Official audit-record definition.
- [ ] Retention and audit access-control policy.
- [ ] Projection/availability SLA and alert thresholds.
- [ ] Reconciliation of admission-only indeterminate attempts.

