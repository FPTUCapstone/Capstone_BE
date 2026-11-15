# TM-89 BE-03 production-readiness checklist

Evidence snapshot: 2026-11-10 (Asia/Saigon). This checklist is an activation gate, not
authorization to merge, deploy, migrate, restart, or change Railway/Redis. PC-05 production
acceptance must not be claimed until every required item is evidenced and approved.

Status legend: **VERIFIED** means directly evidenced from an authorized source; **NOT_VERIFIED**
means no current production evidence was accessible. Owner statements are identified separately
and are not treated as a current runtime reading.

## Evidence register

| Area | Status | Evidence |
|---|---|---|
| PR #146 | VERIFIED | Ready for Review at `7bdadaa59733d59c641aa410c31e0018f6bda606`; exact-head CI succeeded; `Han126-Phuc2004` review remains requested with no submitted review. |
| `develop` protection | VERIFIED | GitHub requires one approving review and enforces protection for administrators. No required status-check context is configured, so CI success must remain an explicit release gate. |
| Railway source trigger | VERIFIED (2026-11-09 UI evidence) | Production `Capstone_BE` is connected to `FPTUCapstone/Capstone_BE`, branch `develop`, with **Auto deploys when pushed to GitHub** enabled. |
| Railway CLI/API access | NOT_VERIFIED | Official CLI 5.71.0 was invoked read-only on 2026-11-10; `railway status --json` returned `Unauthorized`. No login, token extraction, or broader permission request was attempted. |
| Railway deployment history/logs | NOT_VERIFIED | CLI authorization is absent. GitHub Deployments API returned no production records and repository webhooks returned none; those surfaces cannot prove Railway deployment history. |
| Redis service/volume | VERIFIED (2026-11-09 UI evidence) | Railway showed an online `Redis` service with `redis-volume`. This does not prove persistence settings or restore viability. |
| Redis runtime durability | NOT_VERIFIED | Version, `appendonly`, `appendfsync`, persistence directory, AOF health and restart recovery were not readable. Owner-provided earlier evidence says AOF was off, RDB-only and no backup; recheck is mandatory before activation. |
| Redis memory/eviction | NOT_VERIFIED | `maxmemory`, `maxmemory-policy`, used/peak memory, fragmentation, volume usage and capacity headroom were not readable. No production load or benchmark was run. |
| Redis backup/restore | NOT_VERIFIED | No backup artifact, schedule, retention, restore owner or successful restore drill was accessible. No `BGSAVE`, restart or persistence mutation was run. |
| Shared Redis workload | NOT_VERIFIED | The application also references Redis for distributed scheduling rate limits, but current production client count, command rate, latency and resource limits were not accessible. |
| SQL provider/control path | VERIFIED | Repository uses the manual-only `DB migrate (Azure SQL)` workflow; it does not run on PR/push and accepts only `develop`. Environment `azure-db` requires reviewer `HoangTrinhyeuoi`. |
| SQL migration history | VERIFIED, limited | Recent manual workflow runs exist. Run `42403262096` completed successfully with `DRY_RUN=false` and skipped already-recorded `20260918_add_audit_result_reason.sql`. This is not evidence that the TM-89 migration is applied. |
| Production `AuditLogs` shape | NOT_VERIFIED | No authorized read-only SQL session was available. Presence/shape of `scan_attempt_id`, `journal_event_id` and `UX_AuditLogs_JournalEventId` remain unknown. |
| TM-89 migration safety | VERIFIED in code/tests only | `20261105_add_checkin_audit_journal_identity.sql` uses `XACT_ABORT`, a transaction, drift checks, nullable GUID columns, duplicate detection and a filtered unique index. Production backup, dry-run and schema inventory remain absent. |
| Monitoring | NOT_VERIFIED | Railway has Logs/Observability surfaces, but current dashboards/alerts for append/WAITAOF, backlog age/depth, pending, conflicts, ACK misses, failures and admission-only age were not accessible. In-process counters alone are not an operational dashboard. |
| Rollback capability | NOT_VERIFIED | Railway native deployment history was inaccessible. No evidence proves that a restorable deployment remains within the plan's retention window or that Redis/SQL restore has been rehearsed. |

## Merge and deployment coupling

- [x] Repository default `CheckInAuditJournal:Enabled=false` deliberately returns MSG127 before
  verification or ticket mutation. It does not preserve legacy check-in behavior.
- [x] Railway production auto-deploys pushes to `develop`.
- [x] GitHub merge requires one human approval, but branch protection does not require the CI context.
- [ ] Release Owner explicitly disables GitHub auto-deploy in Railway Service Settings before merge.
- [ ] A second operator verifies auto-deploy is disabled, the deployment queue has no waiting/skipped
  item that can later run, and records current active deployment ID/SHA/time.
- [ ] During the release freeze, no one applies staged Railway changes, invokes Deploy Latest Commit,
  Redeploy/Restart/Rollback, edits networking, or changes service configuration.
- [ ] After merge, Railway deployment history is checked to prove no deployment was created.

Disabling GitHub auto-deploy controls new branch-push deployments only. Railway documentation states
that variable/service changes are staged and redeploy affected services when applied; manual redeploy,
rollback/restart, networking changes, queued deploys and Railway-initiated host migrations are separate
paths. Therefore the release procedure requires both auto-deploy suspension and an owner-enforced
Railway change freeze with deployment-history monitoring.

## Redis durability and capacity

- [ ] Authorized read-only evidence records Redis version and confirms `WAITAOF` support.
- [ ] Authorized read-only evidence records `appendonly`, `appendfsync`, persistence directory and
  current AOF status without changing them.
- [ ] Infrastructure Owner approves persistent AOF on the mounted volume and provides isolated
  restart/recovery evidence for the intended production configuration.
- [ ] Authorized read-only evidence records `maxmemory`, eviction policy, used/peak memory,
  fragmentation, volume usage, connected clients and workload latency.
- [ ] Infrastructure Owner approves a no-loss eviction/capacity plan for immutable streams and receipt
  hashes. `noeviction` or any alternative is not selected by this checklist.
- [ ] Capacity evidence covers shared rate-limiter load, unbounded audit backlog during SQL outage and
  measured append + WAITAOF p50/p95/p99. No SLA or retention value is inferred.
- [ ] Backup schedule, artifact ownership, access controls and a successful restore drill are evidenced.

## SQL migration

- [ ] DBA executes a read-only production inventory for `dbo.AuditLogs`, both GUID columns, existing
  values, duplicate non-null event IDs and all relevant indexes/filters.
- [ ] DBA proves a restorable Azure SQL backup/PITR point or BACPAC and names the restore owner.
- [ ] The manual workflow first runs with `dry_run=true` from reviewed `develop`; its plan is approved.
- [ ] The migration is rehearsed against a production-shaped restored copy.
- [ ] A separate SQL Migration Approval authorizes the real workflow run. Code/merge approval does not.
- [ ] Post-run read-only verification confirms nullable GUID columns and the filtered unique index.

## Projection, poison handling and monitoring

- [x] Pending recovery is bounded, advances an `XAUTOCLAIM` cursor and reserves fresh-event capacity.
- [x] Malformed/conflicting events remain pending: no ACK, `XDEL`, `XTRIM`, expiry or silent discard.
- [x] ACK=0 is recorded separately and is not reported as successfully processed.
- [ ] Production telemetry exports journal availability, append/WAITAOF latency, stream depth/oldest age,
  pending depth, conflicts, ACK misses, projection failures/retries and admission-only age.
- [ ] Operations Owner approves alert routing and a poison/conflict investigation runbook. The runbook
  must not authorize deletion/ACK/trim without a later approved policy.

## Controlled activation and smoke gates

- [ ] Human code review is approved on the exact PR HEAD.
- [ ] Merge Approval is granted only after auto-deploy suspension and release-freeze verification.
- [ ] Redis Infrastructure Change Approval and SQL Migration Approval are independently complete.
- [ ] Controlled Deployment Approval names the release operator, observer, exact merge SHA, window,
  stop conditions and recovery owner.
- [ ] Deployment uses the durable journal configuration; successful start proves configuration binding
  without logging connection strings.
- [ ] Staging tests cover successful check-in, known rejection, journal unavailable/full fail-closed,
  SQL outage/recovery replay, duplicate/conflict, poison fairness and Redis restart.
- [ ] Production smoke is non-destructive: one approved success and one approved known rejection, with
  SQL projection/backlog/latency observation. Do not deliberately stop production Redis/SQL.

Stop immediately for: unsupported Redis/WAITAOF, missing restore evidence, AOF or volume mismatch,
eviction/data-loss risk, insufficient headroom, migration drift/duplicates, missing monitoring, latency
regression, poison/backlog growth, incorrect API result, ticket mutation during infrastructure failure,
or any unplanned deployment.

## Service recovery strategy (BE-A05 remains authoritative)

1. Incident Commander stops new QR check-in by keeping the BE-03 build fail-closed; journal uncertainty
   must return MSG127 and must never become `InvalidQr` or legacy best-effort audit.
2. Release Operator suspends further planned changes and records the active deployment/config IDs.
3. Redis/DB Owners preserve volumes, stream entries, receipts and SQL evidence; no ACK/delete/trim/flush.
4. If the current BE-03 deployment is healthy but the journal is not, keep `Enabled=false` under an
   approved incident change and communicate the QR check-in interruption. This is a deliberate outage,
   not degraded unaudited operation.
5. Recover Redis/AOF or SQL, verify backlog and replay idempotently, then re-enable only after the
   Infrastructure Owner and Incident Commander approve recovery evidence.
6. Prefer fix-forward on the BE-03 build. Do not use Railway rollback to a pre-BE-03 image: Railway
   rollback restores both the prior image and its custom variables and could silently restore the legacy
   audit path. The additive SQL columns/index remain in place unless a DBA separately approves rollback.

## Independent owner approvals

| Approval | Owner | Required evidence | Status |
|---|---|---|---|
| Code Review Approval | Authorized Backend reviewer (`Han126-Phuc2004` currently requested) | Review on exact HEAD; CI evidence considered explicitly because branch protection does not require it | PENDING |
| Merge Approval | Repository/Release Owner | Code approval, exact-head CI, auto-deploy disabled, queue empty, Railway change freeze active | PENDING |
| Redis Infrastructure Change Approval | Railway/Redis Infrastructure Owner | Current runtime survey, backup/restore, AOF/volume plan, memory/eviction/capacity and rollback | PENDING |
| SQL Migration Approval | DBA/Data Owner plus `azure-db` required reviewer | Schema inventory, backup/PITR proof, dry-run and restored-copy rehearsal | PENDING |
| Controlled Deployment Approval | Release Owner + Operations Owner | Exact SHA/config, maintenance communication, smoke plan, observers, stop/recovery procedure | PENDING |
| Production Activation Approval | Task Owner + Infrastructure/DB/Operations owners | All prior approvals complete and post-deploy evidence green | PENDING |
| Product Policy Approval | Product Owner/Security as applicable | Official audit record, retention/access, projection/availability SLA, admission-only reconciliation | PENDING |

No approval in this table implies any other approval.

## Product policies still PENDING

- [ ] Official audit-record definition.
- [ ] Retention and audit access-control policy.
- [ ] Projection/availability SLA and alert thresholds.
- [ ] Reconciliation of admission-only indeterminate attempts.

## Next authorized action

The smallest next action is for the existing Railway Project/Infrastructure Owner to establish an
already-authorized, least-privilege read-only CLI/API session (or run the same inventory and return
sanitized outputs), and for the DBA to provide read-only `AuditLogs` inventory plus backup/restore
evidence. Until those artifacts exist, merge, infrastructure change, migration, deployment and
activation remain **BLOCKED**.
