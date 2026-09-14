# UC-69 View Audit Log Details Spec

## Status

Ready for Implementation (Screen #34: System Audit Log Details View)

## Scope

This specification defines the Backend implementation for **UC-69: View Audit Log Details** (Screen #34 System Audit Log Details View).

It covers strictly:
- Retrieving full detail view for a specific system audit log entry by `id` from `dbo.AuditLogs`.
- Fetching complete event payload including `beforeData` (JSON before change) and `afterData` (JSON after change).
- Joining with `dbo.Users` via `LEFT JOIN` to retrieve actor credentials (`Email`, `FullName`, `Role`) while gracefully handling system-triggered events (`actor_user_id = null`).
- Formatting output timestamps in alignment with CR-07 timezone requirements (`Asia/Ho_Chi_Minh` UTC+7).
- Enforcing role-based access control (Administrator role required per BR-115).

> [!NOTE]
> - **UC-68 Scope Separation:** List view and filtering across audit log entries is owned by **UC-68: View Audit Logs**.
> - **UC-67 Scope Separation:** Exporting audit logs / report generation is owned by **UC-67: Export Statistical Reports**.

---

## Actor

- **Administrator**

---

## Preconditions

1. Caller is authenticated.
2. Caller holds the `Administrator` role (`UserRole.Administrator`).

---

## Application Messages & Validation Rules

| Message ID | Type | Content / Usage |
|---|---|---|
| **MSG126** | Authorization Error | `"Access denied. Administrator role required."` *(Returned on 403 Forbidden per BR-115)* |
| **MSG127** | System Error | `"The audit log details cannot be retrieved because of a system or network failure."` *(Returned on 500/Internal Server Error)* |
| **MSG129** | Business Error / Not Found | `"System audit log entry not found."` *(Returned on 404 Not Found when requested log ID does not exist)* |

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
  "success": true,
  "statusCode": 200,
  "message": "Audit log detail retrieved successfully.",
  "data": {
    "id": 501,
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
    "createdAtUtc": "2026-09-13T10:00:00Z",
    "createdAtLocal": "13/09/2026 17:00:00"
  },
  "errors": null
}
```

---

## Data Join & System Action Null Safety

1. **`LEFT JOIN` Execution:**
   - In EF Core, `dbContext.AuditLogs.AsNoTracking().Include(a => a.ActorUser)` performs a `LEFT OUTER JOIN dbo.Users ON AuditLogs.actor_user_id = Users.user_id`.
2. **System Actions & Null Actors (`actor_user_id = null`):**
   - System-triggered events (e.g. background batch jobs, automated cancellations, scheduled tasks) or deleted user accounts have `actor_user_id = null`.
   - The query handler projects null actors safely:
     - `ActorUserId`: `long?` (`null` when system-triggered)
     - `ActorEmail`: `string?` (`null` when system-triggered)
     - `ActorFullName`: `user?.FullName ?? "System"`
     - `ActorRole`: `user?.Role` (`null` when system-triggered)

---

## Timezone Alignment (CR-07)

- **Storage & Processing:** Database column `created_at` operates in **UTC (`DateTimeOffset`)**.
- **Display Standard (CR-07):** DTO provides both `createdAtUtc` (ISO 8601 UTC) and `createdAtLocal` formatted according to Vietnam Time (`Asia/Ho_Chi_Minh`, UTC+7) in `dd/MM/yyyy HH:mm:ss` format.

---

## Error Handling & Error Codes

| Error Code | HTTP Status | Description |
|---|---|---|
| `admin.audit_log_forbidden` | `403` | Caller is not an Administrator (`MSG126`) |
| `admin.audit_log_not_found` | `404` | Audit log entry with specified ID does not exist (`MSG129`) |

---

## Acceptance Criteria

1. Administrator can request full detail view of any audit log entry by ID via `GET /api/v1/admin/audit-logs/{id}`.
2. Full event payload including `beforeData` and `afterData` JSON strings is returned.
3. System-triggered events (`actor_user_id = null`) display `"System"` as actor name without exceptions.
4. Non-existent log ID returns `404 Not Found` with `MSG129`.
5. Non-administrators receive `403 Forbidden` (`MSG126`).
6. 100% unit test pass rate for `GetAuditLogDetailQueryHandler`.
