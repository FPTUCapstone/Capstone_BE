# TM-207 — Atomic implementation plan

Status: **APPROVED — 2026-09-26; TASKS 1–3 COMPLETE — 2026-09-27**
Spec: `specs/TM-207-spec.md` (approved 2026-09-26)
Branch: `feature/datmnt-tour-media-management-api`

## Task record, boundaries, and baseline

- Jira: TM-207 — Implement Operator Tour Media management API.
- Repository: `FPTUCapstone/Capstone_BE`.
- Base: `origin/develop` at
  `525489b61ee2a2791b3d0cf71313e80a4a0445e7`.
- Worktree: `D:\CapStone\Capstone_BE_tm207`.
- Required predecessor: TM-206 is present on the base and owns the canonical
  `commerce.TourMedia` table, soft-delete lifecycle, active-order/primary
  indexes, and parent-Tour cascade behavior.
- Source decisions: approved TM-205 catalogue-enrichment decision and the
  approved `TM-207-spec.md` contract.
- Process sources reviewed before planning: repository `AGENTS.md` and
  `CLAUDE.md`; the applicable engineering/checklist rules remain mandatory.
- Local Cloudinary configuration was verified by key name only in the ignored
  `.env`: `Cloudinary__CloudName`, `Cloudinary__ApiKey`,
  `Cloudinary__ApiSecret`, and `Cloudinary__TourMediaFolderRoot` are present
  and non-empty. Values must never be printed, committed, copied into test
  results, or included in logs/ProblemDetails.
- Scope is Backend and SQL Server only. It excludes Web/Mobile UI, Tour
  publication/versioning implementation, Admin media-review endpoints, POI
  photos, booking flows, and EF migrations.
- No implementation task starts until this plan is explicitly approved. No
  commit, push, PR, Jira transition, or deployment is implicit in approval.

Approval record: the owner explicitly approved this plan with
`approve TM-207 plan`. Approval authorizes Task 1 only under the completion
boundary below.

**Baseline commands**

```powershell
dotnet restore TripMate.slnx
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet build TripMate.slnx -c Release --no-restore
dotnet test TripMate.slnx -c Release --no-build --logger "console;verbosity=minimal"
git diff --check
git status --short --branch
```

**Baseline evidence — 2026-09-26 (Asia/Ho_Chi_Minh)**

- .NET SDK `10.0.401`; runtime `10.0.12`.
- The first sandboxed restore could not reach NuGet (`NU1301`); the identical
  command was rerun with approved network access and passed, exit code 0.
- Format verification passed, exit code 0.
- Release build passed, exit code 0, with 0 warnings and 0 errors.
- Full tests passed at process level, exit code 0: 931 passed, 0 failed,
  87 skipped, 1,018 total. Breakdown: Application Unit Tests 596 passed;
  Infrastructure Unit Tests 73 passed; API Integration Tests 262 passed and
  87 skipped. The integration assembly took approximately 9 minutes 39 seconds.
- The 87 SQL Server skips were caused by missing
  `TRIPMATE_SQLSERVER_TEST_CONNECTION`; they are not evidence that SQL behavior
  is green. Before Task 1, configure an isolated, identified SQL Server test
  database and record only its safe server/database identity, never credentials.

## Approved implementation invariants

1. SQL Server stores metadata and lifecycle state only; Cloudinary owns raw
   image bytes and delivery.
2. Operator endpoints are role-gated to `TourOperator`, derive the actor from
   the authenticated principal, and never accept ownership from the payload.
3. Draft/Rejected Tours allow all mutations; Pending is read-only;
   Approved/Inactive allow list and alt-text-only corrections. Other material
   changes return `409` until a versioned publication workflow exists.
4. At most ten active images is an application workflow rule enforced under a
   Tour-scoped serializable/locking strategy. It is not a SQL count/CHECK
   constraint.
5. Normal removal soft-deletes the row, compacts active order, enqueues delayed
   provider cleanup, and writes success audit data in one database transaction.
   Physical parent Tour deletion keeps the TM-206 cascade behavior.
6. Upload requires idempotency scoped by actor + Tour + operation key. A
   replay with the same fingerprint returns the original result and never
   creates a second Cloudinary asset; a different fingerprint returns `409`.
7. Provider calls never occur inside an open long-running SQL transaction.
   Persisted upload state and an opaque preallocated provider public ID make
   retry/recovery deterministic across processes.
8. The API never returns `cloudinary_public_id`, secrets, signatures, raw
   provider responses, internal lifecycle timestamps, or other Operators'
   identifiers.

## Task 1 — Define the TM-207 schema contract (Red)

**Files**

- Add
  `tests/TripMate.Api.IntegrationTests/Tours/TourMediaManagementMigrationTests.cs`.
- Add a scoped pre-TM-207 fixture under
  `tests/TripMate.Api.IntegrationTests/Fixtures/Database/` based on the
  canonical post-TM-206 schema.
- Update the integration-test project copy rules only if the existing fixture
  convention does not already include the new SQL file.

**Work**

1. Write fresh-schema and recorded-upgrade inventory tests for:
   - `TourMedia.alt_text NVARCHAR(500) NOT NULL`;
   - global uniqueness of `cloudinary_public_id`;
   - a persisted upload-operation table with actor, Tour, idempotency key,
     SHA-256 payload fingerprint, opaque provider public ID, state, optional
     completed media reference, timestamps, and a unique actor/Tour/key scope;
   - a persistent cleanup-outbox table with media/public ID, not-before time,
     attempt/lease state, bounded retry metadata, timestamps, and lookup index.
2. Prove fresh and upgraded inventories are identical, migration rerun is a
   no-op, and same-named wrong-shape tables/columns/constraints/indexes cause a
   full rollback instead of silent acceptance.
3. Seed a representative existing TM-206 row. Prove the upgrade assigns a
   deterministic nonblank compatibility alt text without changing its Tour,
   URL, caption, order, primary state, lifecycle, or identity. The application
   must require explicit valid alt text for every new upload.
4. Add constraint tests for duplicate public IDs, duplicate upload operation
   keys, invalid operation/outbox states, retry bounds, FK behavior, and
   existing-data preservation. Keep the TM-206 soft-delete/cascade/index
   contract unchanged.

**Verification**

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~TourMediaManagementMigrationTests" --logger "console;verbosity=minimal"
```

**Definition of done**

Tests are discovered and fail only because the TM-207 schema/migration does
not yet exist. A skip caused by missing SQL connectivity is not an acceptable
RED result.

**Measured RED evidence — 2026-09-27**

- SQL Server identity: `localhost,14330`, bootstrap catalog `master`; the
  harness exclusively managed random databases with prefix `TripMate_Test_`.
  Credentials were loaded in-process from an ignored local environment file
  and were not printed or persisted.
- Focused command: `dotnet test
  tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj
  -c Release --no-restore --filter
  "FullyQualifiedName~TourMediaManagementMigrationTests" --logger
  "console;verbosity=minimal"`.
- Exit code 1; 9 discovered, 9 failed, 0 passed, 0 skipped.
- Expected failures only: the fresh schema reports missing
  `TourMedia.alt_text`; upgrade/constraint tests report the absent
  `20260927_add_tour_media_management.sql` migration (including negative tests
  that currently receive `FileNotFoundException` before their future SQL shape
  assertion).
- Preflight connection attempts first exposed an invalid empty builder result
  and then unsupported local encryption. Both were corrected before evidence
  capture; the final run reached SQL Server and created its isolated databases.
- A post-run query found no remaining `TripMate_Test_%` database.
- Task 2 production schema/migration work has not started.

## Task 2 — Implement the upgrade-safe SQL inventory (Green)

**Files**

- Modify `database/tripmate_schema_v7.sql`.
- Add `database/migrations/<implementation-date>_add_tour_media_management.sql`.
- Update `database/README.md` only where the migration/runbook inventory needs
  the new script documented.

**Work**

1. Add the approved `alt_text` and unique provider-ID guarantee to the full
   schema without changing the existing TM-206 lifecycle/ordering contract.
2. Add the upload-operation and cleanup-outbox tables from Task 1. Store no
   file bytes, API secrets, signatures, or raw Cloudinary payloads.
3. Implement a transactional, additive, idempotent SQL migration with complete
   shape validation, trusted constraints, named indexes/FKs, and fail-safe
   rollback. Do not add an EF migration.
4. Backfill only the new non-null alt-text requirement for legacy TM-206 rows
   with the explicitly tested compatibility value; do not invent business
   metadata for new rows or mutate unrelated catalogue data.
5. Keep the maximum-ten rule out of SQL. Keep `ON DELETE CASCADE` only where
   the approved ownership/lifecycle contract requires it; cleanup history must
   not become an accidental path that blocks or broadens Tour deletion.

**Verification**

Run the Task 1 filter against the identified SQL Server test database.

**Definition of done**

All schema-contract tests pass with 0 failed and 0 skipped, including full
fresh/upgrade inventory parity, rerun, rollback, and preservation cases.

**Measured GREEN evidence — 2026-09-27**

- Added canonical TM-207 inventory to `tripmate_schema_v7.sql` and the
  transactional/idempotent
  `20260927_add_tour_media_management.sql` upgrade path.
- TM-207 SQL suite: 11 passed, 0 failed, 0 skipped. This includes fresh schema,
  post-TM-206 upgrade parity, migration rerun, full migration-chain replay,
  wrong-shape rollback, preservation, uniqueness/state constraints, and
  cleanup survival after physical Tour cascade.
- TM-206 regression suite: 9 passed, 0 failed, 0 skipped. Its migration and
  inventory test now explicitly recognize only the approved additive TM-207
  `alt_text` column and provider-ID index, so `apply-schema.sh` can safely
  replay all migrations after the schema evolves.
- The maximum-ten rule remains absent from SQL. No Domain/EF, API, Cloudinary
  adapter, UI, commit, push, or deployment work was performed in Task 2.

## Task 3 — Add domain entities and EF mappings (Red → Green)

**Files (planned)**

- Add `TourMedia`, `TourMediaUploadOperation`, and
  `TourMediaCleanupOutboxItem` entities plus narrowly scoped enums in
  `src/TripMate.Domain/`.
- Add corresponding configurations under
  `src/TripMate.Infrastructure/Persistence/Configurations/`.
- Update `Tour`, `IApplicationDbContext`, `ApplicationDbContext`, and every
  repository test double that implements the context contract.
- Add persistence/model tests under
  `tests/TripMate.Infrastructure.UnitTests/Persistence/` and domain tests under
  `tests/TripMate.Application.UnitTests/Features/TourMedia/` where appropriate.

**Work**

1. First add failing tests for lengths, UTC fields, enum storage, FKs, indexes,
   query filters/explicit active predicates, navigation ownership, and private
   mutation boundaries.
2. Model explicit operations for metadata edit, reorder/primary selection,
   soft delete, upload-operation state transitions, and cleanup retry/complete;
   reject invalid transitions rather than exposing public setters.
3. Preserve zero-or-one active primary behavior. Do not require exactly one
   image or primary at the database/domain persistence layer.
4. Map every name/type/length/default/filter to the SQL contract and prove the
   EF relational model matches the canonical inventory.

**Verification**

```powershell
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --no-restore --filter "FullyQualifiedName~TourMedia"
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --no-restore --filter "FullyQualifiedName~TourMedia"
```

**Definition of done**

The new model is persistence-compatible, all invalid transitions are covered,
and no API/provider behavior has leaked into Domain.

**Measured RED/GREEN evidence — 2026-09-27**

- RED: both approved `FullyQualifiedName~TourMedia` filters exited 1 during
  compilation because the three TM-207 entities and status enums did not yet
  exist. A test-only namespace qualification issue exposed by that first run
  was corrected before production implementation.
- Added `TourMedia`, `TourMediaUploadOperation`, and
  `TourMediaCleanupOutboxItem` with guarded lifecycle/state transitions,
  private mutation setters, navigation ownership, and narrowly scoped enums.
- Added SQL-compatible EF configurations for every TM-207 column, UTC
  converter, string enum, default, FK/delete behavior, check constraint,
  named index/filter, and the active-media query filter. Updated `Tour`, the
  application context contract, the production context, and all three context
  test doubles.
- Focused Application domain suite: 7 passed, 0 failed, 0 skipped, exit 0.
- Focused Infrastructure persistence suite: 4 passed, 0 failed, 0 skipped,
  exit 0. Existing `TourPersistenceModelTests`: 4 passed, 0 failed, 0 skipped.
- Full Application unit tests: 603 passed, 0 failed, 0 skipped, exit 0. Full
  Infrastructure unit tests: 77 passed, 0 failed, 0 skipped, exit 0.
- `dotnet format TripMate.slnx --verify-no-changes --no-restore` passed after
  applying the repository formatter. Release solution build passed with
  0 warnings and 0 errors. `git diff --check` passed.
- Review pass 1 found no spec/scope violation; review pass 2 found no
  unresolved code-quality, persistence, or secret-handling finding. No API,
  Cloudinary adapter/configuration, worker, or EF migration was added.

## Task 4 — Define and implement the Cloudinary boundary (Red → Green)

**Files (planned)**

- Add application ports/value objects for image inspection, media storage, and
  upload/cleanup results under `TripMate.Application/Common/Interfaces` or the
  feature-local equivalent.
- Add strongly typed, validated Cloudinary options and adapter under
  `TripMate.Infrastructure/Media/Cloudinary/`.
- Update Infrastructure DI, `.env.example`, and `docker-compose.yml` with key
  names/placeholders only.
- Add adapter/options/log-redaction tests under Infrastructure unit tests.

**Work**

1. Add failing contract tests for JPEG/PNG/WebP magic/decode checks, positive
   dimensions, 24-megapixel limit, 10-MiB limit, corrupt/polyglot rejection,
   cancellation, HTTPS delivery URLs, and provider error classification.
2. Select a maintained Cloudinary .NET package compatible with `net10.0`, pin
   its version in the project, and record the package/security check. Do not
   hand-roll request signing.
3. Implement signed server-side upload with folder
   `{configuredRoot}/{tourId}`, a server-generated opaque UUID public ID,
   overwrite disabled, original filename excluded, metadata stripped, and no
   unsigned upload preset.
4. Implement safe destroy with CDN invalidation for the cleanup worker. Map
   transient/provider rejection without exposing provider bodies or config.
5. Validate required options at startup outside tests that replace the adapter.
   Never load `.env` by parsing it in application code; Compose/deployment/user
   secrets remain the configuration providers.

**Definition of done**

Adapter tests use controlled fakes/stubs and contain no secret. Invalid config
fails closed; safe logs and errors reveal no credentials/signatures/raw body.

**Measured RED/GREEN evidence — 2026-09-27**

- RED: the focused Infrastructure media filter exited 1 during compilation
  because the approved application media ports, Cloudinary adapter/options,
  and image inspector did not yet exist. Production implementation followed
  that failing contract.
- Added application-only image inspection/storage contracts and an
  Infrastructure Cloudinary boundary. Uploads are signed server-side through
  `CloudinaryDotNet` 1.29.3, use opaque UUID public IDs under
  `{configuredRoot}/{tourId}`, disable overwrite and filename reuse, and never
  use an unsigned preset. Destroy requests always request CDN invalidation.
- Added SkiaSharp 4.152.1 plus its Linux no-dependencies native asset for
  magic/decode validation and metadata-stripping re-encode. ImageSharp 4 was
  rejected during implementation because its build requires a Six Labors
  license key; no ImageSharp reference or implementation remains.
- The inspector enforces JPEG/PNG/WebP only, declared MIME/extension matching,
  exact container boundaries against trailing polyglot payloads, positive
  dimensions, the 24-megapixel limit, the 10-MiB encoded limit, cancellation,
  and safe corrupt-file rejection.
- Focused Release Infrastructure media suite: 38 passed, 0 failed, 0 skipped,
  exit 0. Tests use controlled fakes only; no real Cloudinary request ran.
- Full Release solution suite: Application 603 passed; Infrastructure 110
  passed; API Integration 262 passed, 98 SQL-gated tests skipped, 0 failed;
  exit 0. The SQL-specific TM-207 migration suites had already passed against
  the identified SQL Server database in Tasks 1–2.
- `dotnet list ... package --vulnerable --include-transitive` reported no
  vulnerable package from the configured NuGet source. Cloudinary and
  SkiaSharp versions are pinned in the Infrastructure project.
- Required options are bound with startup validation. `.env.example` and
  Compose contain key names/placeholders only; the ignored `.env` was not
  printed or changed. `docker compose config --quiet` passed with test-only
  placeholder environment values.
- `dotnet format TripMate.slnx --verify-no-changes --no-restore` and
  `git diff --check` passed. Review pass 1 found no scope/spec violation;
  review pass 2 found no unresolved security, secret/log-redaction, package,
  or code-quality finding. No API endpoint, application workflow, cleanup
  worker, UI, commit, push, or live provider smoke call was added.

## Task 5 — Implement idempotent upload workflow (Red → Green)

**Files (planned)**

- Add upload command, validator, handler, response DTO, error codes, and
  feature services under
  `src/TripMate.Application/Features/TourMedia/Upload/` and `Common/`.
- Add a SQL Server Tour-scoped lock abstraction/implementation only if the
  existing transaction abstraction cannot prove the required concurrency.
- Add application unit tests and SQL Server concurrency/integration tests.

**Work**

1. Test role/account/profile eligibility, ownership non-disclosure, Tour-state
   matrix, file and metadata validation, maximum ten active rows, next
   contiguous order, optional primary conflict, audit, cancellation, and all
   error mappings before implementation.
2. Hash decoded/validated content plus normalized caption, alt text, and
   primary intent. Persist/claim actor + Tour + key with an opaque provider ID
   before the provider call so concurrent workers cannot allocate two assets.
3. Same key/fingerprint returns the stored completed DTO. Same key/different
   fingerprint returns `409`. An in-progress retry uses the same public ID and
   safe state transition; it never blindly uploads a second asset.
4. Perform provider I/O outside the SQL transaction. Then use a short
   Tour-scoped serializable transaction to recheck ownership/state/limit,
   insert media, complete the operation, and write the success audit.
5. If provider upload succeeds but SQL completion fails, attempt immediate
   best-effort destroy; persist a cleanup item when immediate compensation is
   not confirmed. Never report success unless media, operation, and audit are
   committed.

**Definition of done**

Unit and SQL concurrency tests prove one logical operation, one media row, and
at most one provider asset for concurrent same-key requests; two different
keys cannot exceed ten active images.

**Measured RED/GREEN evidence — 2026-09-27**

- RED: the focused Application upload test filter initially exited 1 because
  the command, validator, handler, response/error contracts, and upload lock
  did not exist.
- Added the application upload workflow with content-plus-normalized-metadata
  SHA-256 idempotency fingerprints, a persisted actor/Tour/key claim and opaque
  provider public ID before provider I/O, completed-result replay, payload
  mismatch conflict, and safe retry using the stored public ID.
- Provider I/O is outside database transactions. SQL Server uses transaction-
  owned `sp_getapplock` resources in fixed `Tour -> operation` order; completion
  uses a short serializable Tour lock and rechecks authorization, lifecycle,
  primary, capacity, and order before committing media, operation, and audit.
- If database completion fails after a provider success, the handler attempts
  immediate destruction and records an orphan cleanup outbox item only when
  deletion cannot be confirmed. No provider detail or public identifier enters
  a response or audit record.
- Focused Application upload suite: 16 passed, 0 failed, 0 skipped, exit 0.
  Full Release Application suite: 619 passed, 0 failed, 0 skipped, exit 0.
- Added two real-SQL-Server concurrency tests: same key proves one operation,
  one media row, and one retained provider asset; different keys at nine active
  images prove the final active count remains ten and the losing provider asset
  is compensated. The filter first discovered both tests (0 passed, 0 failed,
  2 skipped, exit 0) when no `TRIPMATE_SQLSERVER_TEST_CONNECTION` was present.
  A second attempt configured that variable only within the test process from
  the existing local developer configuration, without printing credentials;
  it failed before database creation because the local SQL Server endpoint
  timed out and Docker Desktop's engine pipe was unavailable. This is an
  environment prerequisite failure, not an assertion failure. The tests remain
  automatically executable when the repository-supported variable points to a
  reachable SQL Server instance.

## Task 6 — Implement list, metadata edit, reorder/primary, and removal (Red → Green)

**Files (planned)**

- Add feature folders under `src/TripMate.Application/Features/TourMedia/` for
  `List`, `UpdateMetadata`, `Reorder`, and `Delete`.
- Add corresponding unit and SQL Server integration tests.

**Work**

1. List only active owned media, ordered deterministically by
   `sort_order, tour_media_id`, projected to the approved DTO without provider
   IDs/lifecycle internals.
2. Metadata edit trims/normalizes input and compares actual changed fields.
   Approved/Inactive allow alt-text-only changes; caption changes are material
   and return `409`. No client-supplied `isMinor` flag is trusted.
3. Reorder requires each active media ID exactly once, rejects foreign/deleted/
   duplicate/missing/stale sets, and applies contiguous one-based order plus
   zero-or-one requested primary atomically. Use a collision-safe two-phase
   update so the active unique index is never transiently violated.
4. Delete verifies ownership/state, soft-deletes only, clears primary on the
   deleted row, compacts active order, enqueues one 30-day cleanup item, and
   writes safe audit data in the same transaction. It never promotes another
   image and never calls Cloudinary inside the HTTP transaction.
5. Prove rollback on audit/outbox/order failures and prove POI media and other
   Operators' rows are unchanged.

**Definition of done**

Every approved state/ownership/order/deletion branch has a deterministic test;
all writes are atomic and preserve the TM-206 constraints.

**Measured RED/GREEN evidence — 2026-09-27**

- RED: the focused management-handler test filter exited 1 because the List,
  UpdateMetadata, Reorder, and Delete application feature namespaces did not
  exist.
- Added active-only owned listing with deterministic `sort_order, tour_media_id`
  ordering and a public-safe DTO now including both created and updated UTC
  timestamps.
- Added metadata classification: Draft/Rejected permit edits; Pending rejects
  every mutation; Approved/Inactive permit alt-text-only correction and reject
  caption changes. Client input cannot declare itself minor.
- Added serializable Tour-locked reorder with exact-set validation, zero-or-one
  primary, and a two-save temporary-order phase so the filtered unique sort-order
  index is never transiently violated during swaps.
- Added idempotent soft-delete with no implicit primary promotion, contiguous
  active-order compaction, 30-day cleanup-outbox enqueue, and safe audit data
  inside the same production transaction.
- Focused management suite: 9 passed, 0 failed, 0 skipped, exit 0. Full
  Release Application suite: 628 passed, 0 failed, 0 skipped, exit 0.
- The real-SQL-Server reorder/delete test was discovered (0 passed, 0 failed,
  1 skipped, exit 0) but remains gated on the same unreachable local SQL Server
  prerequisite recorded for Task 5. It executes automatically when
  `TRIPMATE_SQLSERVER_TEST_CONNECTION` targets a reachable instance.

## Task 7 — Expose the secured RFC 7807 API and OpenAPI contract (Red → Green)

**Files (planned)**

- Add `OperatorTourMediaController` under
  `src/TripMate.Api/Controllers/V1/` with route
  `/api/v1/operator/tours/{tourId}/media`.
- Add thin request models/multipart binding helpers and any narrowly scoped
  operation/schema filter required by Swashbuckle.
- Extend `ApiControllerBase.HandleFailure` or a feature mapper with explicit
  TM-207 codes/statuses, without changing unrelated feature semantics.
- Add endpoint, JWT/role, multipart-limit, ProblemDetails, and OpenAPI tests.

**Work**

1. Write endpoint tests first for list/upload/PATCH/order/DELETE success and
   400/401/403/404/409/413/503/500 paths.
2. Apply `[Authorize(Roles = nameof(UserRole.TourOperator))]`; derive actor ID
   from `ICurrentUserService`. Traveler and Administrator tokens are denied.
3. Require a valid `Idempotency-Key` for upload, enforce an HTTP request limit
   slightly above the 10-MiB file cap for multipart overhead, stream/copy with
   bounded memory, and pass cancellation through all layers.
4. Return only the approved DTO. Ensure ValidationProblemDetails/RFC 7807 has
   stable feature error codes and never includes stack traces, secrets, raw
   provider errors, public IDs, or non-owned resource detail.
5. Verify generated OpenAPI correctly documents multipart input, header,
   batch reorder payload, `long` IDs, timestamps, and all response statuses.

**Definition of done**

Real authorization middleware and application ownership checks are both
tested. The contract matches the approved spec exactly and introduces no
unsigned/direct-client Cloudinary upload path.

**Measured RED/GREEN evidence — 2026-09-27**

- Added the Tour Operator-only controller for GET, multipart POST, metadata
  PATCH, complete-order PUT, and soft-delete DELETE. The actor ID is taken from
  `ICurrentUserService`; an owner check still runs in every application handler.
- Upload requires a parseable non-empty `Idempotency-Key`, requires file and
  alt text, and caps both the request and multipart body at 11 MiB to allow
  multipart overhead above the 10-MiB image policy. Cancellation is passed
  through the handlers and provider boundary.
- Feature errors map to RFC 7807 statuses: operator access 403; ownership/media
  non-disclosure 404; state/order/capacity/primary/idempotency conflict 409;
  provider unavailable/rejected 503; persistence failure 500. Responses use
  the approved DTO and tests verify no Cloudinary public identifier or provider
  body leaks.
- OpenAPI tests verify all five routes, multipart binary file schema, required
  idempotency header, long media IDs, DTO timestamps, and upload response codes
  including 201, 400, 413, and 503.
- Task-specific API endpoint suite: 10 passed, 0 failed, 0 skipped, exit 0.
  It covers unauthenticated/wrong-role access, owner success across all five
  endpoints, repeated soft-delete, non-owner 404, locked-Tour 409 before
  provider I/O, redacted provider 503, missing upload inputs, and OpenAPI.
- Full Release Application suite: 628 passed. Full Release Infrastructure
  suite: 110 passed. `dotnet format --verify-no-changes` and `git diff --check`
  passed after formatting.
- A full API integration run was started but interrupted after more than eight
  minutes because the existing suite's sequential `WebApplicationFactory`
  startup did not finish in a practical local run. SQL Server gated tests were
  skipped because no reachable SQL Server test endpoint is available. The
  focused TM-207 HTTP/OpenAPI suite completed successfully; the complete API
  and SQL verification remains for Task 9.

## Task 8 — Implement persistent 30-day cleanup processing (Red → Green)

**Operational decisions approved — 2026-09-27**

- Retry transient failures up to the existing default of eight attempts using
  exponential delay starting at one minute and capped at 24 hours.
- A permanent provider rejection moves directly to `Exhausted` for manual
  handling; an already-absent provider asset is treated as successful cleanup.
- The hosted worker runs by default. Poll interval and lease duration are
  validated configuration, with safe defaults; no Cloudinary secrets are
  duplicated in worker options.

**Files (planned)**

- Add cleanup options, service, lease/claim abstraction if needed, and hosted
  worker under `TripMate.Infrastructure/Services` or `Media/Cloudinary`.
- Register the worker conditionally through strongly typed options.
- Add deterministic TimeProvider-based unit tests and SQL Server lease tests.

**Work**

1. Test that items are invisible before `not_before_utc`, independently
   claimed across workers, retried with bounded exponential backoff, and moved
   to an exhausted/manual state after the approved maximum attempts.
2. Destroy the Cloudinary asset with invalidation, then atomically mark the
   item complete. Provider “already absent” is success; transient failure is
   retryable; permanent unsafe/unclassified failure is recorded without raw
   payload or credentials.
3. Worker shutdown/cancellation must release or expire leases safely. A failed
   cleanup never restores a soft-deleted media row and never changes API
   deletion success retroactively.
4. Cover the case where the parent Tour/TourMedia row was physically cascaded:
   the outbox retains the minimum provider identifier needed to finish cleanup
   without retaining the image binary.

**Definition of done**

Tests prove delayed deletion, single-claimer behavior, bounded retries, safe
recovery, and no secret leakage.

**Implementation and verification — 2026-09-27**

- Added the default-on hosted cleanup worker with validated poll interval,
  lease duration, and batch size configuration; Compose/.env examples contain
  values only, no provider credentials.
- Added SQL Server due-row claiming with `UPDLOCK`, `READPAST`, and `ROWLOCK`;
  expired claims receive a new lease token, and all post-provider state changes
  re-lock and fence on the current token in a transaction. Cloudinary calls
  happen only after claim commit, never inside a SQL transaction.
- Deleted and already-absent provider results complete the outbox item;
  transient failures use exponential backoff (1 minute doubling, capped at
  24 hours) and exhaust after eight attempts; permanent/unclassified failures
  go directly to manual `Exhausted` state. Cancellation leaves the lease to
  expire safely, and cleanup never changes the soft-deleted media row.
- Updated the canonical schema and TM-207 migration/model state constraint so
  permanent failures may become `Exhausted` before consuming all retry
  attempts, while every in-progress/exhausted row must have a valid attempt
  count. SQL tests cover that state rule, 30-day due time, concurrent claim,
  lease reclaim, and stale-token fencing.
- RED/GREEN evidence: the new retry-policy test initially failed compilation
  because the policy did not exist; after implementation, the cleanup-focused
  Infrastructure suite passed 20 tests (then 21 after the integer-overflow
  cap case was added). Full Application tests passed 630; full Infrastructure
  tests passed 131; the Task 7 operator endpoint suite passed 10.
- Release solution build passed with 0 warnings/errors; format verification
  and `git diff --check` passed.
- SQL verification completed against an isolated, disposable SQL Server
  container using a generated per-run credential and loopback-only port
  mapping; the user's existing containers/databases were not used. All 14
  targeted SQL tests passed with zero skips: 3 cleanup claim/lease tests and
  11 TM-207 migration/schema tests (fresh schema, upgrade parity, idempotency,
  rollback, constraints, and cascade preservation). Tests were run individually
  and sequentially because the full xUnit parallel batch caused host TCP
  timeouts; the isolated test also exposed and fixed SQL Server's normalized
  `BETWEEN` representation in the migration shape assertion. The temporary
  container was stopped and removed automatically after the run.
- Prior focused Application, Infrastructure, API, Release build, and format
  gates remain as recorded above. Full end-to-end API suite and real
  Cloudinary smoke-test review are still assigned to Task 9; they are not
  represented as complete by the SQL verification.

## Task 9 — Full SQL/API/provider verification and review

**Progress — 2026-09-27 (not complete)**

- Sequential cleanup claiming and barrier-based tests were added after review
  found that parallel provider work would concurrently use the scoped EF
  `DbContext`.
- Application and Infrastructure unit tests pass (630 and 132 respectively);
  the opt-in Cloudinary smoke test is skipped in the default suite.
- The live Cloudinary smoke attempt returned sanitized HTTP 401. Do not treat
  provider verification as passed until the development credentials/config are
  corrected and the smoke test succeeds.
- The full suite before the final SQL fixes completed at 1,034 passed, 0
  failed, and 105 skipped. After the fixes, both full unit suites and all
  focused TM-207 SQL suites were rerun successfully.
- Focused SQL verification now passes 26/26 with zero skips across TourMedia
  migration, management migration, management, upload concurrency, and cleanup
  outbox tests. It ran against a disposable SQL Server 2022 container with a
  generated credential and `TripMate_Test_<GUID>` databases; the container,
  generated password, and environment variables were removed afterwards.
- SQL verification exposed and fixed two test/code issues: inspect soft-deleted
  media with `IgnoreQueryFilters`, and clear claim-time EF tracking before
  completion so a concurrent same-key request observes a completed operation.
- `dotnet restore`, format verification, Release build, and vulnerability scan
  succeeded. Current NuGet sources reported no vulnerable packages.
- The Cloudinary smoke attempt returned HTTP 401. The Cloudinary account owner
  must verify the development API key/secret configuration before another
  live provider run.
- Current evidence and exact limitations are recorded in
  `docs/TM-207-verification.md`. Independent review and successful live
  Cloudinary verification remain outstanding. No Task 10 is defined in this approved plan;
  the next coordination step is completing the existing Draft PR for review.

**Files**

- Add `docs/TM-207-verification.md` with reproducible final-head evidence.
- Review every file touched by Tasks 1–8; do not add unrelated cleanup.

**Work**

1. Run all focused unit/API/SQL suites against an identified isolated SQL
   Server database with zero TM-207 skips. Add barrier-based concurrency tests,
   not timing/sleep assertions.
2. Run one labelled, opt-in Cloudinary smoke test against the configured
   non-production folder: upload a generated harmless test image, verify HTTPS
   delivery metadata, destroy it, and confirm cleanup. The test output records
   only safe cloud/folder identity and generated public ID hash; it never
   prints secrets, signatures, full config, or user media URLs.
3. Perform two review passes: spec/contract compliance, then code quality,
   transaction/concurrency/security/logging/resource cleanup.
4. Map the current engineering/cross-review checklist to evidence. Mark
   irrelevant items `N/A` with a reason; do not call skipped checks passed.

**Final verification commands**

```powershell
dotnet restore TripMate.slnx
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet build TripMate.slnx -c Release --no-restore
dotnet test TripMate.slnx -c Release --no-build --logger trx
dotnet list TripMate.slnx package --vulnerable --include-transitive
git diff --check
git status --short --branch
```

Record exact commands, exit codes, pass/fail/skip counts, SQL Server safe
identity, Cloudinary smoke-test cleanup outcome, final HEAD/base SHA, and any
accepted limitation. Re-run verification after every review fix.

**Definition of done**

- All unit, endpoint, SQL Server, and selected provider checks pass.
- TM-207-critical tests have 0 failures and 0 skips.
- No high/critical vulnerable package or unresolved Critical/Major review
  finding remains.
- Fresh and upgraded database inventories match.
- No secret or binary is tracked, logged, returned, or stored in SQL Server.
- Only then may the owner separately authorize commit, push, PR creation,
  deployment, or Jira transition.

## Completion boundary

Plan approval authorizes starting Task 1 only. Each task follows
RED → minimal GREEN → focused regression → scope review and reports evidence
before advancing. Stop on an unresolved contract change, unexpected baseline
failure, missing SQL/provider isolation, or any risk of exposing credentials.
