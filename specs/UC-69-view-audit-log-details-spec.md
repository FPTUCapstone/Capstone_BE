# UC-69 View Audit Log Details Backend Spec

## Status

Updated for SRS Alignment (Screen #34: System Audit Log Entry Detail View)

## Scope

This specification defines the Backend implementation for **UC-69: View Audit Log Details** (Screen #34 System Audit Log Entry Detail View).

It covers strictly:
- Retrieving full detail view for a specific system audit log entry by `id` from `dbo.AuditLogs`.
- Fetching complete event payload including `beforeData` (JSON before change) and `afterData` (JSON after change).
- Returning structured audit metadata: `result` (Success/Failure), `clientPlatform`, `affectedModule`, `affectedEntity`, `affectedEntityId`, `reason` (Supplied Reason).
- Joining with `dbo.Users` via `LEFT JOIN` to retrieve actor credentials (`Email`, `FullName`, `Role`) while gracefully handling system-triggered events (`actor_user_id = null`).
- Formatting output timestamps in alignment with CR-07 timezone requirements (`Asia/Ho_Chi_Minh` UTC+7 `dd/MM/yyyy HH:mm:ss`).
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
2. Caller holds the `Administrator` role (`UserRole.Administrator`) per BR-115.

---

## Application Messages & Business Rules

| Message / Rule ID | Type | Content / Usage |
|---|---|---|
| **BR-115** | Authorization Rule | Only an account holding the Administrator role may read an audit log entry. |
| **BR-130** | Immutability Rule | The audit log entry is immutable; its recorded content is presented exactly as it was written. |
| **BR-03** | Sensitive Masking Rule | A password value, token value, and payment credential are never present unmasked. |
| **BR-119** | Context Reason Rule | When the recorded action required a reason, that reason is part of the presented entry. |
| **MSG126** | Authorization Error | `"Access denied. Administrator role required."` *(Returned on 403 Forbidden per BR-115)* |
| **MSG127** | System Error | `"The audit log details cannot be retrieved because of a system or network failure."` *(Returned on 500 Internal Server Error)* |
| **MSG128** | No Field Change Info | `"No field changes recorded for this entry."` *(Returned in Change Table area when entry records no field change)* |
| **MSG150** | Not Found Error | `"The selected audit log entry does not exist."` *(Returned on 404 Not Found when requested log ID does not exist)* |

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
    "result": "Success",
    "actorUserId": 1,
    "actorEmail": "admin@tripmate.vn",
    "actorFullName": "System Administrator",
    "actorRole": "Administrator",
    "sourceAddress": "192.168.1.10",
    "clientPlatform": "Web (Chrome/Windows)",
    "affectedModule": "TourOperatorManagement",
    "affectedEntity": "OperatorProfile",
    "affectedEntityId": 42,
    "beforeData": "{\"status\":\"PendingApproval\",\"businessLicenseUrl\":\"https://storage...\"}",
    "afterData": "{\"status\":\"Active\",\"approvedAtUtc\":\"2026-09-13T10:00:00Z\"}",
    "reason": "Business license verified and verified compliant by Compliance Officer.",
    "createdAtUtc": "2026-09-13T10:00:00Z",
    "createdAtLocal": "13/09/2026 17:00:00"
  },
  "errors": null
}
```

---

## Error Handling & Error Codes

| Error Code | HTTP Status | Description |
|---|---|---|
| `admin.audit_log_forbidden` | `403` | Caller is not an Administrator (`MSG126`) |
| `admin.audit_log_not_found` | `404` | Audit log entry with specified ID does not exist (`MSG150`) |

---

## Acceptance Criteria

1. Administrator can request full detail view of any audit log entry by ID via `GET /api/v1/admin/audit-logs/{id}`.
2. Full event payload including `result`, `clientPlatform`, `affectedModule`, `beforeData`, `afterData`, and `reason` is returned.
3. System-triggered events (`actor_user_id = null`) display `"System"` as actor name without exceptions.
4. Non-existent log ID returns `404 Not Found` with `MSG150`.
5. Non-administrators receive `403 Forbidden` (`MSG126`).
6. 100% unit test pass rate for `GetAuditLogDetailQueryHandler`.
