# TM-84 / UC-38 Create Coupon Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow an approved Tour Operator to create a reusable coupon for one or more of their approved tours.

**Architecture:** Add a voucher aggregate and tour-association entity mapped to the existing `commerce.Vouchers` and `commerce.VoucherApplicableTours` tables. A CQRS create command authorizes the caller, validates all selected tours before writing, and persists the aggregate atomically; the API remains a thin Tour Operator endpoint.

**Tech Stack:** .NET 10, C#, MediatR, FluentValidation, EF Core/SQL Server, xUnit, FluentAssertions.

**Spec:** `specs/TM-84-spec.md`

## Global Constraints

- Use the existing database-first schema; do not generate an EF migration or alter the canonical SQL schema unless implementation proves it is insufficient.
- Restrict the endpoint to an authenticated Tour Operator with an active user account and approved operator profile.
- Coupon scope is one or more explicitly selected, caller-owned `Approved` tours only.
- Store codes as uppercase; enforce a 3–30 ASCII letter/digit/hyphen format.
- Use `Result`/`Result<T>` for expected failures and standard ProblemDetails mapping.
- Use one transaction; no partial voucher or association rows may survive failure.

## Review Focus

- Case-only code collision must return a deterministic 409, including a concurrent insert race.
- A mixed list of owned and foreign/missing/ineligible tours must leave no coupon row behind.
- Percentage and flat discount fields must not be silently accepted in contradictory combinations.
- UTC validity windows near the current instant must reject an already-ended coupon without timezone conversion drift.
- `applicableTourIds` must reject null, empty, non-positive, and duplicate values before persistence.

---

### Task 1: Voucher domain model and EF mapping

**Files:**
- Create: `src/TripMate.Domain/Entities/Voucher.cs`
- Create: `src/TripMate.Domain/Entities/VoucherApplicableTour.cs`
- Create: `src/TripMate.Domain/Enums/VoucherDiscountType.cs`
- Create: `src/TripMate.Domain/Enums/VoucherStatus.cs`
- Create: `src/TripMate.Infrastructure/Persistence/Configurations/VoucherConfiguration.cs`
- Create: `src/TripMate.Infrastructure/Persistence/Configurations/VoucherApplicableTourConfiguration.cs`
- Modify: `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs`
- Modify: `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs`
- Test: `tests/TripMate.Domain.UnitTests/Entities/VoucherTests.cs`
- Test: `tests/TripMate.Infrastructure.UnitTests/Persistence/VoucherPersistenceModelTests.cs`

**Interfaces:**
- Produces: `Voucher.Create(long ownerOperatorUserId, string code, VoucherDiscountType discountType, decimal discountValue, decimal? maxDiscountAmount, decimal minOrderAmount, int? usageLimit, int? usageLimitPerUser, DateTimeOffset validFromUtc, DateTimeOffset validToUtc, IReadOnlyCollection<Tour> applicableTours, DateTimeOffset createdAtUtc)`.
- Produces: `DbSet<Voucher> Vouchers` and `DbSet<VoucherApplicableTour> VoucherApplicableTours` on `IApplicationDbContext`.

- [ ] **Step 1: Write failing domain and EF model tests**

Cover valid percentage/flat factories, rejected invariant combinations, initial `Active`/zero-use state, approved-tour links, table/column names, unique code index, UTC datetime mappings, and the composite association key.

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test tests/TripMate.Domain.UnitTests/TripMate.Domain.UnitTests.csproj --filter FullyQualifiedName~VoucherTests` and `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --filter FullyQualifiedName~VoucherPersistenceModelTests`

Expected: FAIL because voucher types/mapping do not exist.

- [ ] **Step 3: Implement the aggregate, association, enums, DbSets, and EF configurations**

Map exactly to the existing commerce tables and constraints. Use navigation properties when adding a new voucher with its associations in one `SaveChangesAsync` call.

- [ ] **Step 4: Run the focused tests to verify they pass**

Run the two commands from Step 2.

Expected: PASS.

### Task 2: Create-coupon command validation and handler

**Files:**
- Create: `src/TripMate.Application/Features/Coupons/Common/CouponErrorCodes.cs`
- Create: `src/TripMate.Application/Features/Coupons/Create/CreateCouponCommand.cs`
- Create: `src/TripMate.Application/Features/Coupons/Create/CreateCouponCommandValidator.cs`
- Create: `src/TripMate.Application/Features/Coupons/Create/CreateCouponResponse.cs`
- Create: `src/TripMate.Application/Features/Coupons/Create/CreateCouponCommandHandler.cs`
- Test: `tests/TripMate.Application.UnitTests/Features/Coupons/Create/CreateCouponCommandValidatorTests.cs`
- Test: `tests/TripMate.Application.UnitTests/Features/Coupons/Create/CreateCouponCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IApplicationDbContext.Vouchers`, `IApplicationDbContext.Tours`, `IApplicationDbContext.OperatorProfiles`, `ICurrentUserService`, `IDateTimeProvider`, and `Voucher.Create(...)` from Task 1.
- Produces: `IRequest<Result<CreateCouponResponse>> CreateCouponCommand` with code, discount inputs, limits, UTC validity window, and applicable tour IDs.

- [ ] **Step 1: Write failing validator tests**

Assert accepted percentage and flat payloads; reject code format, empty/null/duplicate/non-positive tour IDs, invalid enum, contradictory max-cap values, non-positive limits, invalid validity window, expired end time, excessive decimal scale, and a flat discount above a supplied minimum order.

- [ ] **Step 2: Implement and verify `CreateCouponCommandValidator`**

Run: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter FullyQualifiedName~CreateCouponCommandValidatorTests`

Expected: PASS.

- [ ] **Step 3: Write failing handler tests**

Cover unauthenticated/non-operator/inactive-profile rejection; successful normalized creation; foreign/missing/non-approved tour rejection; no partial write for mixed scope; existing code conflict; and database unique-constraint race translated to `coupon.code_conflict` after clearing failed tracked entities.

- [ ] **Step 4: Implement `CreateCouponCommandHandler.Handle`**

Authenticate from the current-user service, load the active approved operator, load every requested tour as tracked entities, distinguish not-found from not-owned/ineligible conditions as specified, create the aggregate, save all rows in one transaction, and translate unique-code database violations to the defined conflict result.

- [ ] **Step 5: Run handler tests**

Run: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter FullyQualifiedName~CreateCouponCommandHandlerTests`

Expected: PASS.

### Task 3: Operator coupon endpoint and contract tests

**Files:**
- Create: `src/TripMate.Api/Controllers/V1/OperatorCouponsController.cs`
- Create: `tests/TripMate.Api.IntegrationTests/Coupons/CreateCouponEndpointTests.cs`
- Test: `tests/TripMate.Api.IntegrationTests/Coupons/CreateCouponSqlServerTests.cs`

**Interfaces:**
- Consumes: `CreateCouponCommand` and `CreateCouponResponse` from Task 2.
- Produces: `POST /api/v1/operator/coupons`, returning 201 for success and standard failure ProblemDetails.

- [ ] **Step 1: Write failing endpoint tests**

Assert 401 unauthenticated, 403 traveler/admin, 201 for an approved owner with persisted response, 400 validation failure, 403/404 ownership/reference behavior, 409 case-insensitive conflict, and Swagger route/schema/status descriptions.

- [ ] **Step 2: Implement the thin controller endpoint**

Apply `Authorize(Roles = nameof(UserRole.TourOperator))`, send the request-bound command through MediatR, and map success to 201 and expected failures through `HandleFailure`.

- [ ] **Step 3: Run endpoint tests**

Run: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter FullyQualifiedName~CreateCouponEndpointTests`

Expected: PASS.

- [ ] **Step 4: Write and run real SQL Server tests**

Seed two approved operators and approved/ineligible tours. Prove persistence/relationship FKs, case-insensitive uniqueness, duplicate-code concurrency, atomic rollback, and UTC values using the repository’s non-skipped SQL Server fixture.

Run: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter FullyQualifiedName~CreateCouponSqlServerTests`

Expected: PASS with zero skipped tests.

### Task 4: Full quality gates and scope review

**Files:**
- Modify only files created/changed by Tasks 1–3 when quality gates reveal UC-38 defects.

**Interfaces:**
- Consumes: completed UC-38 API and persistence model.
- Produces: a clean, reviewable Backend branch with no unrelated changes.

- [ ] **Step 1: Run format and whitespace checks**

Run: `dotnet format TripMate.slnx --verify-no-changes --no-restore` and `git diff --check`

Expected: PASS.

- [ ] **Step 2: Run the complete Backend suite**

Run: `dotnet test TripMate.slnx -c Release`

Expected: PASS with no skipped SQL Server tests when Docker configuration is available.

- [ ] **Step 3: Self-review the diff against `specs/TM-84-spec.md`**

Confirm no booking, redemption, payment, coupon application, list/edit/delete endpoint, UI, or schema migration slipped into the change.

