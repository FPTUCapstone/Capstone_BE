# UC-69 View Audit Log Details Backend Spec

## Status

Approved & Aligned with Database Schema (`dbo.AuditLogs`) & Clean Architecture

## Scope

This specification defines the Backend implementation for **UC-69: View Audit Log Details** (Screen #34 System Audit Log Entry Detail View).

It covers strictly:
- Database schema compliance (`dbo.AuditLogs` in `tripmate_schema_v7.sql`).
- Retrieving the exact recorded payload by `id` from `dbo.AuditLogs` without virtual/fake columns.
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
| **BR-130** | Immutability Rule | The audit log entry is immutable; its recorded content is presented exactly as it was written. |
| **MSG126** | Authorization Error | `"Access denied. Administrator role required."` *(Returned on 403 Forbidden per BR-115)* |
| **MSG127** | System Error | `"The audit log details cannot be retrieved because of a system or network failure."` *(Returned on 500 Internal Server Error)* |
| **MSG129** | Not Found Error | `"System audit log entry not found."` *(Returned on 404 Not Found when requested log ID does not exist)* |

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
  "afterData": "{\"status\":\"Active\",\"approvedAtUtc\":\"2026-09-13T10:00:00Z\",\"reason\":\"Business license verified.\"}",
  "ipAddress": "192.168.1.10",
  "createdAtUtc": "2026-09-13T10:00:00Z",
  "createdAtLocal": "13/09/2026 17:00:00"
}
```

---

## Error Handling & Error Codes

| Error Code | HTTP Status | Description |
|---|---|---|
| `admin.audit_log_forbidden` | `403` | Caller is not an Administrator (`MSG126`) |
| `admin.audit_log_not_found` | `404` | Audit log entry with specified ID does not exist (`MSG129`) |

---

## Acceptance Criteria

1. Administrator can request full detail view of any audit log entry by ID via `GET /api/v1/admin/audit-logs/{id}`.
2. Directly returns `Ok(result.Value)` matching UC-68 envelope style.
3. System-triggered events (`actor_user_id = null`) return `actorFullName = "System"`.
4. Non-existent log ID returns `404 Not Found` with `MSG129`.
5. Non-administrators receive `403 Forbidden` (`MSG126`).
6. 100% unit test pass rate for `GetAuditLogDetailQueryHandler`.
