# UC-68 View Audit Logs Spec

## Status

Ready for Implementation (Updated per Technical Review & SRS Scope Alignment)

## Scope

This specification defines the Backend implementation for **UC-68: View Audit Logs** (Screen #33: System Audit Logs List).

It covers strictly:
- Retrieving a paginated, read-only list of system audit log entries from `dbo.AuditLogs`.
- Filtering audit log entries by:
  - `keyword`: free-text search matched against `ActorUser.Email`, `AffectedEntityId`, or `ActionType`.
  - `actionType`: exact match on `ActionType` (e.g. `ApproveOperatorApplication`, `RejectOperatorApplication`).
  - `actorRole`: exact match on `ActorUser.Role` (`Traveler`, `TourOperator`, `Administrator`).
  - `affectedEntity`: exact match on `AffectedEntity` (e.g. `OperatorProfile`, `TourPackage`).
  - `fromDateUtc` & `toDateUtc`: date range filter on `CreatedAtUtc`.
- Ordering entries descending by event timestamp (`CreatedAtUtc DESC`).
- Handling system-triggered actions and missing actors (`actor_user_id = null`) via `LEFT JOIN` with `dbo.Users`.
- Formatting output timestamps in alignment with CR-07 timezone requirements (`Asia/Ho_Chi_Minh` UTC+7).
- Enforcing role-based access control (Administrator role required per BR-115).

> [!NOTE]
> - **UC-69 Scope Separation:** Deep inspection of a single log entry (`BeforeData`, `AfterData`, technical context) is owned strictly by **UC-69: View Audit Log Details** (Screen #34).
> - **UC-67 Scope Separation:** Exporting audit logs / statistical reports is owned strictly by **UC-67: Export Statistical Reports**.

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
| **MSG29** | Validation Error | `"The submitted Event Date range is logically invalid."` *(Returned when `fromDateUtc > toDateUtc`)* |
| **MSG126** | Authorization Error | `"Access denied. Administrator role required."` *(Returned on 403 Forbidden per BR-115)* |
| **MSG127** | System Error | `"The list cannot be retrieved because of a system or network failure."` *(Returned on 500/Internal Server Error)* |
| **MSG128** | Information | `"No audit log entry matches the submitted criteria."` *(Returned when query yields 0 matching records)* |

---

## API Contract

### Search & List Audit Logs Endpoint

```http
GET /api/v1/admin/audit-logs?keyword={keyword}&actionType={actionType}&actorRole={actorRole}&affectedEntity={affectedEntity}&fromDateUtc={fromDateUtc}&toDateUtc={toDateUtc}&pageNumber=1&pageSize=20
Authorization: Bearer <Admin_JWT>
```

#### Query Parameters

| Parameter | Type | Required | Default | Description / Validation |
|---|---|---|---|---|
| `keyword` | string | No | null | Search term matched against `ActorUser.Email`, `AffectedEntityId`, or `ActionType` |
| `actionType` | string | No | null | Filter by exact action type |
| `actorRole` | enum | No | null | Filter by actor role (`Traveler`, `TourOperator`, `Administrator`) |
| `affectedEntity` | string | No | null | Filter by affected entity type |
| `fromDateUtc` | DateTimeOffset | No | null | Event timestamp lower bound (UTC) |
| `toDateUtc` | DateTimeOffset | No | null | Event timestamp upper bound (UTC) |
| `pageNumber` | int | No | 1 | Page number (1-indexed, minimum 1) |
| `pageSize` | int | No | 20 | Items per page (minimum 1, maximum 100) |

#### Validation Rules
- `pageNumber >= 1`
- `1 <= pageSize <= 100`
- `fromDateUtc <= toDateUtc` when both parameters are provided (returns `422 Unprocessable Entity` with `MSG29` if `fromDateUtc > toDateUtc`).

---

### Success Response (`200 OK`)

```json
{
  "items": [
    {
      "id": 105,
      "actionType": "ApproveOperatorApplication",
      "actorUserId": 1,
      "actorEmail": "admin@tripmate.vn",
      "actorFullName": "System Administrator",
      "actorRole": "Administrator",
      "affectedEntity": "OperatorProfile",
      "affectedEntityId": 42,
      "ipAddress": "192.168.1.1",
      "createdAtUtc": "2026-09-10T14:30:00Z",
      "createdAtLocal": "10/09/2026 21:30:00"
    },
    {
      "id": 104,
      "actionType": "HandleEmergencyCancellation",
      "actorUserId": null,
      "actorEmail": null,
      "actorFullName": "System",
      "actorRole": null,
      "affectedEntity": "TourDeparture",
      "affectedEntityId": 88,
      "ipAddress": null,
      "createdAtUtc": "2026-09-10T12:00:00Z",
      "createdAtLocal": "10/09/2026 19:00:00"
    }
  ],
  "pageNumber": 1,
  "pageSize": 20,
  "totalCount": 2,
  "totalPages": 1,
  "hasPreviousPage": false,
  "hasNextPage": false
}
```

---

## Data Join & System Action Null Safety

1. **`LEFT JOIN` Execution:**
   - In EF Core, `dbContext.AuditLogs.AsNoTracking().Include(a => a.ActorUser)` performs an `LEFT OUTER JOIN dbo.Users ON AuditLogs.actor_user_id = Users.user_id`.
2. **System Actions & Null Actors (`actor_user_id = null`):**
   - System-triggered events (e.g. scheduled background jobs, emergency cancellations, automated refund processing) or deleted user accounts have `actor_user_id = null`.
   - The query handler projects null actors safely:
     - `ActorUserId`: `long?` (`null` when system-triggered)
     - `ActorEmail`: `string?` (`null` when system-triggered)
     - `ActorFullName`: `user?.FullName ?? "System"`
     - `ActorRole`: `user?.Role` (`null` when system-triggered)

---

## Timezone Alignment (CR-07)

- **Storage & Processing:** Database columns `created_at` and query filters `fromDateUtc`/`toDateUtc` operate in **UTC (`DateTimeOffset`)**.
- **Display Standard (CR-07):** DTO provides both `createdAtUtc` (ISO 8601 UTC) and `createdAtLocal` formatted according to Vietnam Time (`Asia/Ho_Chi_Minh`, UTC+7) in `dd/MM/yyyy HH:mm:ss` format.

---

## Error Handling & Error Codes

| Error Code | HTTP Status | Description |
|---|---|---|
| `admin.audit_log_forbidden` | `403` | Caller is not an Administrator (`MSG126`) |
| `admin.audit_log_invalid_date_range` | `422` | `FromDateUtc` is after `ToDateUtc` (`MSG29`) |

---

## Acceptance Criteria

1. Administrator can query audit logs with optional filtering by keyword, action type, actor role, entity, and date range.
2. Log list is returned ordered descending by timestamp (`CreatedAtUtc DESC`).
3. Results are paginated according to page number and page size parameters.
4. System-triggered events (`actor_user_id = null`) are handled safely without errors, displaying `"System"` as the actor name.
5. Non-administrators receive `403 Forbidden` (`MSG126`).
6. Invalid date range (`FromDateUtc > ToDateUtc`) returns `422 Unprocessable Entity` (`MSG29`).
7. 100% green unit test pass rate.
