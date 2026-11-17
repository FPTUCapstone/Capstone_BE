# TM-226 Implementation Plan

## Phase 1 — Database & Domain Model
1. Create canonical SQL migration `database/migrations/20261117_tm226_commission_statements.sql` adding:
   - `commercial.ServiceCommissionStatements` with unique constraint on `(provider_id, period)`.
   - `commission_statement_id` column and index on `commercial.ServiceBookings`.
2. Update `database/tripmate_schema_v7.sql` to maintain schema parity.
3. Implement `ServiceCommissionStatement.cs` entity in `TripMate.Domain/Entities/`.
4. Update `ServiceBooking.cs` with `CommissionStatementId` and assignment logic.
5. Add `ServiceCommissionStatementConfiguration.cs` in `TripMate.Infrastructure/Persistence/Configurations/` and register in `ApplicationDbContext.cs` and `IApplicationDbContext.cs`.

## Phase 2 — Application Core & Business Logic
1. Create `src/TripMate.Application/Features/Providers/CommissionStatements/`:
   - DTOs: `CommissionStatementDto`, `CommissionStatementDetailDto`, `StatementBookingRequestDto`.
   - Error Codes: `CommissionStatementErrorCodes` (`NotFound`, `Forbidden`, `InvalidState`, `InvalidInput`, `AlreadyPaid`, `IdempotencyMismatch`).
   - Generation Service: `ICommissionStatementService` / `CommissionStatementService`:
     - Calculate 10% on Completed requests in previous month (`Math.Floor(actualAmount * 0.10m)`).
     - Idempotent generation with database unique constraint.
     - Overdue checking (due date + 7 days -> Overdue, due date + 15 days -> lock provider account via `User.Lock(...)`).
   - Provider Queries & Commands:
     - `GetProviderStatementsQuery` & Handler (enforces provider ID, filters, pagination).
     - `GetProviderStatementDetailQuery` & Handler (enforces provider ownership, returns booking items).
     - `ReportCommissionPaymentCommand` & Handler (validates reference, status, idempotency).
   - Staff Queries & Commands:
     - `GetReconciliationStatementsQuery` & Handler (filters by provider, period, status, pagination).
     - `GetReconciliationStatementDetailQuery` & Handler.
     - `ConfirmCommissionReceiptCommand` & Handler (verifies payment, status -> Paid, records Staff ID, writes to `AuditLog`).
     - `GenerateMonthlyCommissionStatementsCommand` & Handler (allows triggering generation).
     - `ProcessCommissionOverdueCommand` & Handler (allows triggering overdue/lock processing).

## Phase 3 — Infrastructure & Hosted Background Service
1. Create `CommissionStatementLifecycleHostedService.cs` running scheduled checks for statement generation (on the 1st of month) and daily overdue/locking checks.
2. Register services and hosted service in `DependencyInjection.cs`.

## Phase 4 — Web API Controllers
1. Implement `ServiceProviderStatementsController.cs` (`/api/v1/provider/commission-statements`) with `[Authorize(Roles = nameof(UserRole.ServiceProvider))]`.
2. Implement `AdminCommissionReconciliationController.cs` (`/api/v1/admin/commercial-settlements/reconciliation`) with `[Authorize(Roles = $"{nameof(UserRole.Staff)},{nameof(UserRole.Administrator)}")]`.

## Phase 5 — Frontend Integration & Verification (Next.js)
1. In `Capstone_FE`:
   - Update `src/features/hotel-service-provider/server/workflowProxy.ts` with proxy routes for statements and reconciliation.
   - Update Next.js route handlers:
     - `app/api/provider/statements/route.ts` & `[id]/route.ts` & `[id]/report-payment/route.ts`.
     - `app/api/admin/commercial-settlements/reconciliation/route.ts` & `[id]/route.ts` & `[id]/reconcile/route.ts`.
   - Update `providerStatementService.ts` and `staffReconciliationService.ts` to call the live API via `workflowRequest` (with clean fallback for mock/tests).
   - Verify `CommissionStatementsView.tsx` (Screen 105) and `CommissionReconciliationView.tsx` (Screen 106).

## Phase 6 — Testing & Verification
1. Unit tests:
   - Commission calculation precision (10% rounded down to whole VND).
   - Zero commission for NoShow, Cancelled, Rejected, Expired.
   - Statement generation idempotency.
   - Bank transfer reference validation (MSG172).
   - State transition rules (cannot report for Paid, cannot confirm for Paid).
   - Resource ownership boundary (provider cannot view/pay another's statement).
   - Overdue locking logic (locks after 15 days past due date).
2. Integration / Endpoint tests:
   - Provider statements listing & detail endpoints.
   - Report payment with idempotency key.
   - Staff reconciliation listing, detail, and confirm receipt with audit logging.
3. Frontend verification:
   - Run Vitest tests (`pnpm test` / `npm test`).
   - Run TypeScript typecheck.
