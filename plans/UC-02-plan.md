# UC-02 Register Tour Operator Account Backend Implementation Plan

Status: **Approved design; MSG157-MSG160 use BE constants without message-table seeding; Task 7 local SQL verification completed; PC-03 queue and real Firebase/Cloudinary browser verification remain.** The 2026-10-04 decision is to stop on existing licence duplicates for manual data-owner resolution. Follow the BE specification in `specs/UC-02-spec.md`. Use TDD; rerun checks on the final head before a push.

Branch: `feature/linhnv-register-tour-operator`.

## Task 8 — Owner-decided business identifier formats (2026-10-08)

Add BE field validation and feature-local error codes for the Tax Code and two
Travel Licence Number formats in `RegisterOperatorCommandValidator` and
`AuthErrorCodes`; preserve required/max-length checks. Update UC-02 endpoint
and SQL test fixtures from placeholder identifiers to valid values. Test both
valid types, malformed fields returning 400 before Firebase/storage work, and
existing duplicate/rollback behavior. Coordinate matching FE/Mobile pre-submit
validation and field messages. No database schema change is needed.

## Baseline evidence (2026-10-01)

- Repository: `Capstone_BE`; baseline `aba94657592281122d4d075db67683c55a9f83f8` (`origin/develop` at the recorded time). The FE baseline is recorded separately in its plan.
- `dotnet build TripMate.slnx -c Release` passed.
- `dotnet test TripMate.slnx -c Release`: 222 Infrastructure + 1019 Application + 305 API tests passed; SQL-gated tests skipped locally at this baseline. Re-run after implementation.

## Task 1 — Complete document storage abstraction

**Implemented in the working tree (2026-10-04).** `IOperatorDocumentStorage` and `CloudinaryOperatorDocumentStorage` support upload and `DeleteAsync(publicId, contentType)` for later compensation. PDF uses Cloudinary raw; JPG/PNG use image. Delete tests cover resource-type selection, successful/already-absent deletion, provider failure, and invalid input. The Cloudinary SDK request preserves the existing image-deletion behavior for tour media.

Verification on this working tree: 31 targeted Cloudinary tests passed; the full Release suite passed (245 Infrastructure, 1019 Application, 305 API integration; one Infrastructure and 148 API SQL-gated tests skipped). `dotnet format TripMate.slnx --no-restore --verify-no-changes` and `git diff --check` passed. The initial full run exposed 14 unrelated DI fixture failures after `OperatorDocumentsFolderRoot` became required; both AI test baselines were updated and the full run then passed. This is not final-head or SQL Server-gated evidence; rerun after subsequent tasks and before a push.

## Task 2 — Message constants and schema prerequisite

- MSG157-MSG160 were assigned to UC-02 by the feature owner on 2026-10-04 after checking that the SRS narrative references IDs through MSG156. Keep stable codes in `AuthErrorCodes` and exact feature-local English text in `OperatorRegistrationMessages`; FE maps the codes to the same presentation text. The owner subsequently decided these messages do not need `dbo.Messages` rows, so no message migration is deployed. Preserve locked MSG19/22/23/26. The authored SRS still needs its UC-02 references corrected.
- Add `database/migrations/20261002_add_operator_license_unique.sql`: detect existing duplicate non-empty licence numbers and fail clearly, then create the idempotent unique index `UX_OperatorProfiles_BusinessLicenseNo`. Test a fresh schema, an existing v7 database, and duplicate-data failure. Apply and verify this migration before deploying the endpoint.

Task 2 implementation is complete in the working tree. The licence migration leaves duplicate rows untouched and aborts on collision; EF maps the filtered index. Before the owner removed message-table seeding, the combined filtered SQL test run for `OperatorLicenseMigrationSqlServerTests|OperatorRegistrationMessagesMigrationSqlServerTests` passed **6/6, skipped 0** against isolated databases on the local SQL Server container (`TRIPMATE_SQLSERVER_TEST_CONNECTION`). The message migration and its three tests have since been removed by design; that historical run is **not** final-scope evidence. Only the licence migration remains to apply on deployment databases. Rerun its SQL tests on the final head before a push.

## Task 3 — Contract and validator

Under `src/TripMate.Application/Features/Authentication/RegisterOperator/`, define the command, response, errors and FluentValidation rules. The controller binds `multipart/form-data` with required `firebaseIdToken`, email, password, confirmPassword, companyName, businessLicenseNo, taxCode, contactPerson, businessLicenseDocument and acceptTerms; address, phone and supportingDocuments are optional. Use shared `ApplyEmailRule()` and `ApplyPasswordPolicy()`; locked MSG06 covers password mismatch. Validate PDF/JPG/PNG, 5 MB per file and at most five supporting files before upload. Return field-level errors and the approved message codes.

Tests first: required/optional fields, password and confirmation, file count/type/size, and token presence. The BE response contains MSG08; email delivery and MSG07 presentation belong to the FE after a successful 201.

Task 3 contract and validator implemented in the working tree (2026-10-04): `RegisterOperatorCommand`, `OperatorRegistrationDocument`, `RegisterOperatorResponse`, and `RegisterOperatorCommandValidator` define the multipart data after controller mapping. The validator reuses the shared email/password rules, emits MSG06/157/158 on the relevant fields, enforces schema lengths, accepts PDF/JPG/PNG only when MIME, extension, and leading file signature agree, caps each file at 5 MB and supporting files at five, and requires terms and a Firebase token. The 29 focused validator tests pass. The Release solution run passed 245 Infrastructure, 1048 Application, and 305 API tests, with one Infrastructure and 154 API tests skipped (SQL Server-gated tests were not enabled in this run). `dotnet format TripMate.slnx --no-restore --verify-no-changes` and `git diff --check` passed. HTTP multipart binding, validation-failure serialization, and the `201` response are verified in Task 6 after the handler exists; this Task 3 completion does not claim the endpoint is live.

## Task 4 — Registration handler and compensation

1. Normalize email. Verify `firebaseIdToken` with `IFirebaseAuthService.VerifyIdTokenAsync` and require token email to match the form email **before** upload or insert. An unverified Firebase token binds the Firebase identity; it does not prove that email verification has occurred.
2. Check an existing same-email `PendingApproval` application first (MSG160), then other duplicate email (MSG03), then tax code or licence (MSG159).
3. Upload documents and track each public ID/content type. On every later failure, invoke `DeleteAsync` best-effort for already uploaded files and log any compensation failure.
4. Create one User (`TourOperator`, `PendingApproval`, `EmailVerifiedAtUtc = null`, hashed password, `FullName = contactPerson`), one OperatorProfile (including optional contact address/phone), and OperatorDocuments (`Submitted`) in one database transaction. Use navigation properties for same-transaction relationships.
5. For SQL 2601/2627, classify by violated index/constraint: `UX_Users_Email` → 409 MSG03; `UQ_OperatorProfiles_TaxCode` or `UX_OperatorProfiles_BusinessLicenseNo` → 409 MSG159. Other persistence failures → 503 MSG127. No partial SQL records survive; external files are compensated best-effort.
6. Return 201 with locked MSG08. Do not claim MSG07 was delivered by the BE.

Unit tests first: token invalid/mismatch before upload, uniqueness precedence, each SQL constraint, password/hash/status/mapping, partial-upload and SQL-failure compensation, and success. Distinguish deterministic BE rejection from ambiguous network outcome in FE tests.

Task 4 handler implemented in the working tree (2026-10-04). Firebase identity and form email are compared before upload; duplicate pending applications, other emails, tax codes, and licence numbers are checked before storage. The User/Profile/Document graph is saved through one `ExecuteInTransactionAsync` call with navigation properties. Allocated Cloudinary IDs are compensated best-effort after failed upload, failed SQL save/commit, or cancellation. The SQL Server constraint classifier recognizes only errors 2601/2627 bearing the known email/tax/licence index names; an email race rechecks the committed winner to distinguish MSG160 from MSG03. The focused handler suite passes 18/18. The full Release solution run passed 245 Infrastructure, 1066 Application, and 305 API tests, with one Infrastructure and 154 API tests skipped (SQL Server-gated tests were not enabled in this run). `dotnet format TripMate.slnx --no-restore --verify-no-changes` and `git diff --check` passed. The unit test's `DbUpdateException` classification is mocked and its InMemory transaction does not prove SQL rollback, SQL error classification, or concurrency. Those real-provider checks remain explicitly in Task 7. No endpoint is live until Task 6.

## Task 5 — Verification and sign-in reconciliation

- Amend `WebVerifyEmailCommandHandler` and `ConfirmEmailVerificationCommandHandler`: genuine Firebase verified evidence sets `EmailVerifiedAtUtc` for a `PendingApproval` TourOperator and preserves `PendingApproval`. Existing Locked/Inactive handling stays strict; repeat verification is idempotent.
- Amend `WebPasswordSignInCommandHandler`: after successful password validation, an `AccountStateUnresolved` result for an unverified pending operator can check Firebase's authoritative verified flag through `ConfirmEmailVerificationCommand`, then retry sign-in once. Do not apply this recovery to wrong passwords or other account states.
- Unit/API tests cover `/auth/web/verify-email` with no session issuance, verified operator sign-in, link opened without the original Firebase session, unverified evidence, Locked/Inactive, and repeated verification. The traveler `/auth/verify-email` contract remains unchanged.

Task 5 implemented in the working tree (2026-10-04). Both verification handlers write `EmailVerifiedAtUtc` for a verified `PendingApproval` TourOperator and keep the pending account status. Web password sign-in reconciles only a password-validated, unresolved Tour Operator with a matching pending profile and a missing/implausible marker, then retries login once if Firebase confirms verification. Nine new API integration cases cover verified/unverified evidence, no session during web verification, repeat verification/sign-in, wrong password, missing profile, invalid marker, and Locked/Inactive web verification. The full Release solution run passed 245 Infrastructure, 1066 Application, and 314 API tests, with one Infrastructure and 154 API SQL-gated tests skipped; format verification and `git diff --check` passed. The register→verify→sign-in cross-flow with a real SQL Server remains Task 7; the registration HTTP endpoint itself remains Task 6.

## Task 6 — Controller and OpenAPI

Add guest `POST /api/v1/auth/register/operator` to `AuthController`, binding `firebaseIdToken` and form/files from multipart. Map missing token to 400, invalid/expired Firebase token to 401 `AUTH_TOKEN_INVALID`, token-email mismatch to 400 `AUTH_EMAIL_MISMATCH`, uniqueness to 409, and storage/system failures to 503 through existing `HandleFailure` conventions. Preserve each FluentValidation failure's field and `ErrorCode` (the current global `ValidationException` flattens messages and drops codes); use a scoped endpoint mapping rather than changing unrelated validation responses. Test route, form schema, field-level codes, response envelope/ProblemDetails, and status mappings.

Task 6 implemented in the working tree (2026-10-04). The guest route binds the approved `multipart/form-data` fields, limits total request size to 32 MiB, avoids copying an individual file over 5 MiB, validates through the existing `RegisterOperatorCommandValidator`, and returns field-path → error-code arrays in RFC-7807 400 responses without changing the shared exception middleware. Business failures use `HandleFailure`; the common mapping now returns 409 for MSG159/160 and 503 for MSG127. Explicit form names keep Swagger's multipart schema camel-cased. Sixteen new HTTP/OpenAPI cases pass, covering 201/400/401/409/415/503, indexed supporting-file errors, count limit, and documented fields. The full Release suite passed 245 Infrastructure, 1066 Application, and 330 API tests, with one Infrastructure and 154 API SQL-gated tests skipped. Build passed with zero warnings, and format verification plus `git diff --check` passed. Real SQL Server persistence and cross-flow verification remain Task 7.

## Task 7 — Integration tests

HTTP: 201 happy path; invalid/mismatched Firebase token rejected before storage; 400 field/file errors; 409 email/tax/licence/pending-application collisions; 503 storage failure. SQL Server-gated: real-schema user/profile/document persistence, unique-index collision classification with concurrent requests, rollback and compensation. Run with `TRIPMATE_SQLSERVER_TEST_CONNECTION` before a push and record results. Include a cross-flow test: register → verify → sign in → pending application readable through the existing Admin detail endpoint while the operator workspace remains locked. Report PC-03 queue visibility as an unmet dependency until a collection/list feature is delivered and verified.

Task 7 local SQL Server verification (2026-10-04): `RegisterOperatorSqlServerTests` uses temporary isolated databases with the real v7 schema and current migrations. It verifies fresh-context User/Profile/Document persistence, then register → web email verification → password sign-in → Admin application detail while the profile stays `PendingApproval`. Two concurrent HTTP requests synchronized before `SaveChanges` prove a single committed graph and a `409` for same email (`MSG160`), tax code, and business licence (`MSG159`). A fault injected after SQL writes but before transaction commit proves all three tables roll back and the uploaded object receives a compensation delete. The filtered run `RegisterOperatorSqlServerTests|OperatorLicenseMigrationSqlServerTests` passed **8/8, skipped 0** using the local Docker SQL Server and `TRIPMATE_SQLSERVER_TEST_CONNECTION`; it does not use live Firebase or Cloudinary. Existing HTTP tests cover token/file errors and storage failure with fakes. No BE collection/queue endpoint or FE operator workspace cross-flow was verified here; PC-03 and browser/provider verification remain open.

## Task 8 — Verification and review

1. `dotnet format TripMate.slnx --no-restore --verify-no-changes`
2. `dotnet build TripMate.slnx -c Release`
3. `dotnet test TripMate.slnx -c Release` and local SQL-gated run
4. Spec-compliance review, then code-quality review; record final-head evidence.

Task 8 working-tree verification (2026-10-04): `dotnet build TripMate.slnx -c Release --no-restore` passed with **0 warnings / 0 errors**; `dotnet format TripMate.slnx --no-restore --verify-no-changes` and `git diff --check` passed. The Release solution test run after the Task 7 code change passed **245 Infrastructure, 1066 Application, 330 API**, with **1 Infrastructure and 154 API skipped** because provider-gated tests were not enabled in that run. The separate local SQL Server run for `RegisterOperatorSqlServerTests|OperatorLicenseMigrationSqlServerTests` passed **8/8, skipped 0**. Initial restore-enabled test execution could not read the sandbox-inaccessible user NuGet.Config; the subsequent `--no-restore` build/tests passed using the existing restored assets.

Spec-compliance review: the multipart endpoint, field codes, status mapping, pending User/Profile/Document transaction, licence index, compensation, verification marker, and Admin detail read match the approved BE spec. The four UC-02 messages use BE constants and no message-table migration. Code-quality review found no new P0/P1 issue in this working tree; tests exercise the SQL uniqueness race and rollback after writes. This is **working-tree evidence, not a committed PR-head result**. Re-run checks after committing before reporting final-head evidence. The existing UC-50 Admin detail route can read the created profile, but PC-03 collection/queue visibility is still unimplemented; real Firebase/Cloudinary and FE browser flow have not been exercised. Resolve the PC-03 dependency or approve its scoped exception before claiming full SRS completion.

Non-goals: a new verification provider or sign-in architecture; approval/rejection (UC-50/51); resubmission (UC-03); audit rows.
