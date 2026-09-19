# UC-69 View Audit Log Details Backend Spec

## Status

Schema extended per SOURCE-02 resolution (migration `20260918_add_audit_result_reason.sql`).
`result` and `reason` columns approved and added to `dbo.AuditLogs`.
Historical records retain `null` for both columns — no backfill.

## Scope

This specification defines the Backend implementation for **UC-69: View Audit Log Details** (Screen #34 System Audit Log Entry Detail View).

It covers strictly:
- Database schema compliance (`dbo.AuditLogs` in `tripmate_schema_v7.sql`, extended by migration `20260918_add_audit_result_reason.sql`).
- Retrieving the recorded payload by `id`, masking sensitive response values without changing the stored entry or inventing root columns.
- Exposing `result` (`AuditOutcome` enum serialized as string: `"Success"` / `"Failure"`) and `reason` (free-text, masked at read boundary) when recorded by the writer; both are `null` for historical log entries created before the migration.
- `LEFT JOIN` with `dbo.Users` via `actor_user_id` to retrieve actor details (`Email`, `FullName`, `Role`), with system actions (`actor_user_id = null`) returning `actorFullName = "System"`.
- Timezone standard (CR-07): `createdAtLocal` formatted in Vietnam Time (`Asia/Ho_Chi_Minh` UTC+7) as `dd/MM/yyyy HH:mm:ss`.
- Role authorization (BR-115): Administrator role required.

---

## Preconditions

1. Caller is authenticated.
2. Caller holds the `Administrator` role (`UserRole.Administrator`) per BR-115.

---

## Application Messages & Business Rules

| Message / Rule ID | Type | Content / Usage |
|---|---|---|
| **BR-115** | Authorization Rule | Only an account holding the Administrator role may read an audit log entry. |
| **BR-130** | Immutability Rule | Stored content remains unchanged; BR-03 masking applies only to its read response. |
| **BR-03 / PC-01** | Sensitive values | Passwords, tokens and payment credentials are masked in JSON payloads (`beforeData`, `afterData`) and in the `reason` plain-text field, including nested objects and arrays. |
| **MSG126** | Authorization Error | `"You do not have permission to access this function."` *(Locked SRS 5.3 content — returned on 403 Forbidden)* |
| **MSG127** | System Error | `"TripMate is temporarily unable to process your request. Please check your connection and try again."` *(Locked SRS 5.3 content — returned on 500 Internal Server Error)* |
| **MSG132 (proposed)** | Not Found Error | `"System audit log entry not found."` *(Returned on 404 Not Found when requested log ID does not exist)* |
| **MSG133 (proposed)** | Redaction Notice | `"[REDACTED]"` *(Inline value substituted by `AuditLogPayloadRedactor` when a credential-shaped token is detected in `reason` or in JSON payload fields)* |

> [!NOTE]
> SRS Report 3 §5.3 locks `MSG129` as a generic success toast ("Operation completed successfully.") — it must not be reused as a 404 label. No locked message exists for a not-found audit log entry, so a new message is proposed (`MSG132`) with neutral placeholder wording — **pending developer approval before it is added to the SRS 5.3 list**.

---

## API Contract

### Get Audit Log Details Endpoint

```http
GET /api/v1/admin/audit-logs/{id}
Authorization: Bearer <Admin_JWT>
```

#### Path Parameters

| Parameter | Type | Required | Description |
|---|---|---|---|
| `id` | long | Yes | Unique identifier of the Audit Log entry in `dbo.AuditLogs` |

---

### Success Response (`200 OK`)

```json
{
  "id": 101,
  "actionType": "ApproveOperatorApplication",
  "actorUserId": 1,
  "actorEmail": "admin@tripmate.vn",
  "actorFullName": "System Administrator",
  "actorRole": "Administrator",
  "affectedEntity": "OperatorProfile",
  "affectedEntityId": 42,
  "beforeData": "{\"status\":\"PendingApproval\",\"businessLicenseUrl\":\"https://storage...\"}",
  "afterData": "{\"status\":\"Active\",\"approvedAtUtc\":\"2026-09-13T10:00:00Z\"}",
  "ipAddress": "192.168.1.10",
  "result": "Success",
  "reason": "Business license verified.",
  "createdAtUtc": "2026-09-13T10:00:00Z",
  "createdAtLocal": "13/09/2026 17:00:00"
}
```

> [!NOTE]
> `result` and `reason` are `null` for log entries recorded before migration `20260918_add_audit_result_reason.sql` was applied.
> `reason` is passed through `AuditLogPayloadRedactor.RedactReason()` — if it contains a credential-shaped token it is replaced with `"[REDACTED]"` (MSG133).

---

## Error Handling & Error Codes

| Error Code | HTTP Status | Description |
|---|---|---|
| `admin.audit_log_forbidden` | `403` | Caller is not an Administrator (`MSG126`) |
| `admin.audit_log_not_found` | `404` | Audit log entry with specified ID does not exist (`MSG132` proposed — see note above) |

---

## Acceptance Criteria

1. Administrator can request full detail view of any audit log entry by ID via `GET /api/v1/admin/audit-logs/{id}`.
2. Directly returns `Ok(result.Value)` matching UC-68 envelope style.
3. System-triggered events (`actor_user_id = null`) return `actorFullName = "System"`.
4. Non-existent log ID returns `404 Not Found` (`MSG132` proposed).
5. Non-administrators receive `403 Forbidden` (`MSG126`).
6. `result` and `reason` are included in the response when the log entry was created with `CreateRecordedOutcome`.
7. `result` and `reason` are `null` for legacy entries created with the plain constructor.
8. A `reason` containing a credential-shaped token is returned as `"[REDACTED]"` (MSG133).
9. 100% unit test pass rate for `GetAuditLogDetailQueryHandler`.

## PR #17 remediation acceptance criteria

- Private parameterless domain constructor; the public creation constructor guards existing schema constraints (required names, lengths and positive optional identifiers).
- Response-only redaction uses `[REDACTED]` for credential fields, case-insensitively and ignoring separators. It covers password/hash, token/secret/API key/authorization, and payment credential/card security fields. Nested objects, arrays and serialized JSON strings are processed. Field-change objects naming a sensitive field are withheld as a whole.
- Null payloads remain null. Malformed JSON, scalar payload roots and excessive nesting are withheld rather than returned raw. Non-sensitive object/array fields remain available. This read-boundary defense does not authorize storing credentials in audit entries.
- HTTP tests use the actual JWT bearer pipeline for missing/invalid token (401), non-admin (403), valid admin (200), missing entry (404), System actor, redaction and unchanged persisted payloads.
- No database migration, fabricated Success value or inferred module/platform is introduced.

## Pending decisions

- **SOURCE-02**: Resolved — `result` and `reason` added to `dbo.AuditLogs` via migration `20260918_add_audit_result_reason.sql`. Columns `client_platform` and `affected_module` remain out of scope pending a separate Lead/PO decision.
- **MSG132**: Remains a proposal, not an approved catalog addition. Runtime code is `admin.audit_log_not_found` with neutral wording; `MSG129` must not label this error.
- **MSG133**: Remains a proposal, not an approved catalog addition. Runtime value is `"[REDACTED]"` substituted inline by `AuditLogPayloadRedactor`.

---

## Decision Record — Result/Reason amendment (2026-09-18)

> Approved by the developer on 2026-09-18 (conversation decision record). This record reconciles the UC-68/69 SRS requirements with the physical schema and supersedes the prior SOURCE-02 pending note for this scope. Merged into this spec from the former `UC-69-result-reason-amendment.md`; the FE counterparts of these decisions live in the FE `UC-68-spec.md` / `UC-69-spec.md` amendment sections.

### Schema & write contract

- Nullable `result` VARCHAR(20) constrained to `Success`/`Failure` and `reason` NVARCHAR(1000). No automatic backfill or success default; legacy unknown stays `null`; no reason is inferred from arbitrary JSON.
- The production factory requires an explicit outcome. Existing successful writers (`POI_CREATE`, `ApproveOperatorApplication`) write `Success` in the same unit of work as the business change; `reason` stays nullable for these operations.
- `result` describes execution: a successfully executed rejection is `Success`, not `Failure`. `reason` is business justification, not an exception message. Writers must not submit secrets as business reasons.

### Failure recording (write side)

- Only the two implemented audited commands enter failure recording initially; other audited operations are owned by their UCs, and UC-51 (currently a stub) is not silently implemented here.
- After validation, allowlisted state/reference/duplicate failures create exactly one `Failure` record. Authorization/validation failures are logged separately, never inserted into the business audit. Reads are not audited by this mechanism.
- Confirmed database update/concurrency failures escaping the supported handlers are recorded after their save/transaction has unwound; cancellation or unknown commit outcome is never inferred as `Failure`. No generic HTTP-error-to-audit conversion.
- Failure persistence uses a fresh DI scope/context, never the failed tracked entities; a bounded attempt falls back to structured logging without overriding the original response/exception.
- Failure metadata uses the existing `after_data` JSON container as `{ "auditMetadata": { "errorCode": "..." } }` with `before_data` null. This envelope is event metadata only, not an entity after-snapshot; raw exceptions and submitted request bodies are excluded. The FE labels this block "Failure Context".

### Migration verification requirement

The migration is repeatable and must preserve historical rows. Database-specific checks require a configured SQL test database: apply on an empty DB, on a populated previous-version DB, and re-run once (checklist D05/C19). Do not claim an unapplied script changed the running database.
