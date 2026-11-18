# TM-226 — Monthly Service Commission Statement and Receipt (UC-81–83)

## 1. Authorized Scope and Source of Truth

- **Source of Truth**: SRS §2.2.2, §3.6.6 (UC-82), §3.6.7 (UC-83), §3.10.7 (UC-81 / Non-screen function 19), Table 4 (Screen 105 & 106), Table 7 (Entity 38 `ServiceCommissionStatement`), Business Rules BR-174, BR-196, BR-197, BR-198, BR-199, BR-200, System Messages MSG126, MSG127, MSG133, MSG143, MSG172, MSG207, MSG208, MSG209, MSG210.
- **Roles & Permissions**:
  - `UC-81 Generate Commission Statement`: System background scheduled job (triggered on 1st of each month at 00:00 Vietnam time, plus daily deadline checks).
  - `UC-82 Pay Commission Statement`: `Service Provider` (Screen 105: `/provider/statements`).
  - `UC-83 Confirm Commission Receipt`: `Staff` (Screen 106: `/admin/commercial-settlements/reconciliation`).

## 2. Business Rules & Financial Precision

- **BR-198 (10% Commission Rule)**:
  - Commission owed to TripMate is strictly 10% of the actual amount (`actual_amount`) of each `Completed` service request in the previous calendar month.
  - Calculation: `Math.Floor(actualAmount * 0.10m)`, rounded down to whole VND using exact decimal arithmetic (`decimal`), never floating point.
  - `Rejected`, `Expired`, `Cancelled`, or `NoShow` requests carry NO commission.
- **BR-199 (Monthly Statement Cycle & 7-Day Due Date)**:
  - On the 1st of each month, the system issues exactly ONE statement per eligible Service Provider for requests completed in the previous calendar month (`period` format `YYYY-MM`).
  - Due date is exactly 7 days after issue date: `dueDate = issueDate.AddDays(7)`.
  - Service Provider views their statements and reports the bank transfer reference (`bank_transfer_reference`) within 7 days.
  - Reporting status transition: `Issued` or `Overdue` -> `PaymentReported`.
  - Staff confirms receipt after bank reconciliation: `PaymentReported` -> `Paid`.
- **BR-200 (15-Day Overdue Account Lock)**:
  - Statements unpaid after due date transition to `Overdue` (status update + `MSG207` notification/reminder).
  - When a statement remains unpaid 15 days after its due date (`now >= dueDate.AddDays(15)`), the system automatically locks the Service Provider's user account (`User.Status = Locked`, reason recorded) and closes linked services for booking (`MSG210`).
  - Staff may unlock the account only after every overdue statement has been paid.
- **BR-174 (Immutable Audit Logging)**:
  - Staff receipt confirmation and automated account locking are written to the immutable `AuditLog` with acting account ID and UTC timestamp.
- **BR-193 (Resource Ownership Boundary)**:
  - A Service Provider may view and report payments ONLY for commission statements belonging to their own provider account. Foreign access is rejected with `MSG126` (403 Forbidden).

## 3. Database Schema Contract

- Table: `commercial.ServiceCommissionStatements`:
  - `statement_id BIGINT IDENTITY(1,1) PRIMARY KEY`
  - `statement_code NVARCHAR(32) NOT NULL UNIQUE` (e.g., `STMT-202610-P0001`)
  - `provider_id BIGINT NOT NULL REFERENCES commercial.ServiceProviders(provider_id)`
  - `period VARCHAR(7) NOT NULL` (e.g., `'2026-10'`)
  - `total_requests INT NOT NULL CHECK (total_requests > 0)`
  - `total_actual_amount DECIMAL(14,2) NOT NULL CHECK (total_actual_amount >= 0)`
  - `commission_rate DECIMAL(5,2) NOT NULL DEFAULT 10.00`
  - `commission_amount DECIMAL(14,2) NOT NULL CHECK (commission_amount >= 0)`
  - `issue_date DATE NOT NULL`
  - `due_date DATE NOT NULL`
  - `status VARCHAR(20) NOT NULL DEFAULT 'Issued'` (CHECK: `Issued`, `PaymentReported`, `Paid`, `Overdue`)
  - `bank_transfer_reference NVARCHAR(100) NULL`
  - `reported_at DATETIME2 NULL`
  - `confirmed_by_user_id BIGINT NULL REFERENCES dbo.Users(user_id)`
  - `confirmed_at DATETIME2 NULL`
  - `receipt_number NVARCHAR(50) NULL`
  - `created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()`
  - `row_version ROWVERSION`
  - Unique Constraint: `CONSTRAINT UQ_ServiceCommissionStatements_Provider_Period UNIQUE (provider_id, period)`
- Table: `commercial.ServiceBookings`:
  - `commission_statement_id BIGINT NULL REFERENCES commercial.ServiceCommissionStatements(statement_id)`
  - Index: `IX_ServiceBookings_CommissionStatementId`
  - Invariant: A booking linked to an issued statement (`commission_statement_id IS NOT NULL`) cannot be modified or restated (SRS §3.6.5 PC-02).

## 4. API Endpoints

### Service Provider
- `GET /api/v1/provider/commission-statements`
  - Query params: `status`, `period`, `keyword`, `pageNumber`, `pageSize`
  - Returns paginated statements for authenticated provider.
- `GET /api/v1/provider/commission-statements/{id}`
  - Returns statement details with included completed requests and TripMate bank info.
- `POST /api/v1/provider/commission-statements/{id}/report-payment`
  - Headers: `Idempotency-Key`
  - Body: `{ bankTransferReference: string }`
  - Validates non-empty reference, status in `Issued` or `Overdue`. Updates to `PaymentReported`.

### Staff / Administrator
- `GET /api/v1/admin/commercial-settlements/reconciliation`
  - Query params: `providerId`, `period`, `status`, `keyword`, `pageNumber`, `pageSize`
  - Returns statements across all providers for bank reconciliation.
- `GET /api/v1/admin/commercial-settlements/reconciliation/{id}`
  - Returns statement details, bank reference, provider info, and included completed requests.
- `POST /api/v1/admin/commercial-settlements/reconciliation/{id}/reconcile`
  - Body: `{ notes?: string }`
  - Validates status `PaymentReported` (or `Issued`/`Overdue` with verified transfer). Updates to `Paid`, records Staff ID & timestamp, emits `AuditLog`.
- `POST /api/v1/admin/commercial-settlements/reconciliation/generate` (Admin/Staff maintenance trigger)
  - Body: `{ targetPeriod?: string }` (defaults to previous month)
  - Idempotently generates statements.
- `POST /api/v1/admin/commercial-settlements/reconciliation/process-overdue` (Admin/Staff maintenance trigger)
  - Evaluates overdue transitions and locks accounts 15 days past due date.

## 5. Idempotency & Concurrency

- Statement generation is idempotent: exactly one statement per provider per period. Database unique index `UQ_ServiceCommissionStatements_Provider_Period` enforces this at the storage level.
- App-level locks and transactions prevent race conditions during concurrent statement generation or payment reconciliation.
- Repeat payment report calls with the same idempotency key and payload return the recorded result.
