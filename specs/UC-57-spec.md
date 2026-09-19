# UC-57 Configure Algorithm Parameters Backend Spec

## Status

Approved for implementation (developer instruction, 2026-09-19). Revised from the
original proposal: locked SRS 5.3 message wording, handler-side 422 validation
(CONTRACT-01 lesson from the UC-68/69 review), and audit writes via the approved
`CreateRecordedOutcome` factory (Result/Reason amendment 2026-09-18).

## Scope

This specification defines the Backend implementation for **UC-57: Configure Algorithm Parameters** (Administrator Settings for Algorithm & System Configurations).

It covers:
- Reading and updating algorithm parameters stored in `dbo.SystemConfigs` (`tripmate_schema_v7.sql`).
- Parameter range validation per `MSG118`:
  - `CSP.BufferTimeMinutes`: 5 to 60 minutes (default 15).
  - `CSP.DefaultTravelSpeedKmh`: 10 to 120 km/h (default 30).
  - `Rerouting.SearchRadiusKm`: 1 to 50 km (default 5).
  - `Weather.AlertThresholdSeverity`: 'Moderate' | 'Severe' | 'Extreme' (default 'Severe').
- Recording an audit log entry in `dbo.AuditLogs` (`ActionType = "UpdateAlgorithmParameters"`) upon successful update (`MSG119`).
- Timezone standard (CR-07): `updatedAtLocal` formatted in Vietnam Time (`Asia/Ho_Chi_Minh` UTC+7) as `dd/MM/yyyy HH:mm:ss`.
- Role authorization (BR-115): Administrator role required (`UserRole.Administrator`).

---

## Preconditions

1. Caller is authenticated.
2. Caller holds the `Administrator` role (`UserRole.Administrator`) per BR-115.

---

## Application Messages & Business Rules

| Message / Rule ID | Type | Content / Usage |
|---|---|---|
| **BR-115** | Authorization Rule | Only an account holding the Administrator role may view or update algorithm configurations. |
| **BR-130** | Audit Logging | Updating system configurations creates an immutable audit log entry in `dbo.AuditLogs`. |
| **MSG117** | ToastMessage | `"Algorithm parameters (buffer time, default travel speed, rerouting search radius, and weather thresholds) updated successfully."` |
| **MSG118** | RedUnderTextbox | `"Parameter value out of allowed range (e.g., buffer time must be 5-60 mins)."` |
| **MSG119** | ToastMessage | `"System configuration change recorded in Audit Log."` |
| **MSG126** | Authorization Error | `"You do not have permission to access this function."` *(Locked SRS 5.3 content — returned on 403 Forbidden)* |
| **MSG127** | System Error | `"TripMate is temporarily unable to process your request. Please check your connection and try again."` *(Locked SRS 5.3 content — returned on 500 Internal Server Error)* |

> [!NOTE]
> - Locked SRS 5.3 wording is authoritative for MSG117/118/119/126/127 (Batch 1 reconciliation rule). Do not invent variants.
> - Range/enum validation runs **inside the update handler** and returns `Result.Failure` → `422` with MSG118. Range rules must NOT be FluentValidation rules: `ValidationBehaviour` runs before the handler and would surface them as HTTP 400 (the CONTRACT-01 defect found in the UC-68/69 review).
> - No 404 path exists by design: GET falls back to seeded defaults when a row is missing and PUT upserts — do not invent a not-found contract for this use case.

---

## API Contract

### 1. Get Algorithm Parameters Endpoint

```http
GET /api/v1/admin/system-configs/algorithm-parameters
Authorization: Bearer <Admin_JWT>
```

#### Success Response (`200 OK`)

```json
{
  "bufferTimeMinutes": 15,
  "defaultTravelSpeedKmh": 30,
  "reroutingSearchRadiusKm": 5,
  "weatherAlertThresholdSeverity": "Severe",
  "updatedAtUtc": "2026-09-16T12:00:00Z",
  "updatedAtLocal": "16/09/2026 19:00:00"
}
```

---

### 2. Update Algorithm Parameters Endpoint

```http
PUT /api/v1/admin/system-configs/algorithm-parameters
Authorization: Bearer <Admin_JWT>
Content-Type: application/json
```

#### Request Body

```json
{
  "bufferTimeMinutes": 20,
  "defaultTravelSpeedKmh": 35,
  "reroutingSearchRadiusKm": 10,
  "weatherAlertThresholdSeverity": "Severe"
}
```

#### Success Response (`200 OK`)

```json
{
  "bufferTimeMinutes": 20,
  "defaultTravelSpeedKmh": 35,
  "reroutingSearchRadiusKm": 10,
  "weatherAlertThresholdSeverity": "Severe",
  "updatedAtUtc": "2026-09-16T12:05:00Z",
  "updatedAtLocal": "16/09/2026 19:05:00"
}
```

---

## Error Handling & Error Codes

| Error Code | HTTP Status | Description |
|---|---|---|
| `admin.algorithm_config_forbidden` | `403` | Caller is not an Administrator (`MSG126`) |
| `admin.algorithm_config_invalid_value` | `422` | Parameter value out of allowed range (`MSG118`) |

---

## Acceptance Criteria

1. Administrator can fetch current algorithm configurations via `GET /api/v1/admin/system-configs/algorithm-parameters`.
2. Administrator can update algorithm parameters via `PUT /api/v1/admin/system-configs/algorithm-parameters`.
3. Validates inputs against strict ranges (Buffer: 5-60, Speed: 10-120, Radius: 1-50, Weather: Moderate/Severe/Extreme).
4. Invalid inputs return `422 Unprocessable Entity` with `MSG118`.
5. Successful updates persist to `dbo.SystemConfigs` and automatically log an audit entry in `dbo.AuditLogs` via `AuditLog.CreateRecordedOutcome(..., result: AuditOutcome.Success)` — action type `UpdateAlgorithmParameters`, affected entity `SystemConfig`, `beforeData`/`afterData` JSON containing only the four managed keys.
6. Only the four algorithm keys are read/updated; other `dbo.SystemConfigs` rows (Booking.*, Tour.*, Ticket.*, CommercialService.*) are never touched by this use case.
7. Failed updates (422 validation, 403) write no audit entry and change no config row.
8. `updatedAtUtc` in the GET response is the latest `updated_at` among the four managed rows; `updatedAtLocal` follows CR-07 (`dd/MM/yyyy HH:mm:ss`, Asia/Ho_Chi_Minh).
9. 100% unit test pass rate for Query and Command Handlers; endpoint behavior covered by integration tests (401/403/200/422).
