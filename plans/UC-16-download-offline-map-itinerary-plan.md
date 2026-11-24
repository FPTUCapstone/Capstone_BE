# UC-16 Backend plan
Status: APPROVED DIRECTION — confirmed 2026-11-22. Backend contract verification in progress; no runtime code change is required by the approved reuse approach.
Spec: ../specs/UC-16-download-offline-map-itinerary-spec.md

## Execution order
1. Follow confirmed B1/B2: reuse existing reads, no schema/endpoint changes. Mobile native feasibility remains a release gate.
2. Establish clean baseline: git status; dotnet build TripMate.slnx; dotnet test TripMate.slnx. Record failures before implementation. Configure SQL/Redis integration dependencies using existing test setup without exposing credentials.
3. Verify access/version behavior with existing tests:
   - tests/TripMate.Api.IntegrationTests/Itineraries/GetItineraryDetailEndpointTests.cs
   - tests/TripMate.Application.UnitTests/Features/Itineraries/GetItineraryDetailQueryHandlerTests.cs
   - tests/TripMate.Api.IntegrationTests/PointsOfInterest/GetPoiDetailEndpointTests.cs
   Run dotnet test TripMate.slnx --filter FullyQualifiedName~GetItineraryDetail and the equivalent GetPoiDetail filter.
   Add only meaningful missing regression tests, test-first: active versus inactive group membership, historical ID resolution, denied unrelated Traveler, no mutation on download-related reads.
   Done: current DTO/nullability and exact failure contracts documented for Mobile, tests pass.
4. Pair contract verification with Mobile snapshot tests. Confirm returned ID/version, POI-only changes limitation, missing data behavior and version race. No BE edit is needed if approved contracts already satisfy the requirement.
5. Conditional task ONLY after B1 requires a new query and revised spec approval: define bounded DTO/query/validator/handler under src/TripMate.Application/Features/Itineraries/, expose a read action in existing ItinerariesController, reuse ItineraryAccessService and snapshot read semantics. Specify exact endpoint and files in the revised plan before coding. No external map requests inside a DB transaction, no offline-package table.
6. Review spec compliance then code quality after each changed task, using a fresh reviewer per AGENTS. Finish with dotnet build TripMate.slnx and dotnet test TripMate.slnx; run repository formatting check for changed C# files.
7. Record actual results, remaining provider limitations and contract decisions. No automatic commit, push or merge.

## Delivery gates
- No new endpoint merely to mirror existing reads.
- No EF migration, server package entity, navigation session side effect or authorization relaxation.
- If baseline fails, distinguish existing failure from task regression before advancing.
- If no BE code change is necessary, report contract validation only; do not manufacture implementation work.

## Verification record — 2026-11-22
- Branch confirmed: feature/linhnv-download-offline-map-and-itinerary. Only UC16 spec/plan were untracked before verification; no unrelated source edits.
- dotnet build TripMate.slnx --no-restore -m:1 --disable-build-servers: passed, zero warnings/errors.
- dotnet test TripMate.slnx --no-build --no-restore -m:1 --disable-build-servers --filter "FullyQualifiedName~GetItineraryDetail|FullyQualifiedName~GetPoiDetail|FullyQualifiedName~ItineraryAccess" --logger "console;verbosity=minimal": passed, 12 API integration tests + 29 Application unit tests, zero failures/skips. Other test projects had no matching tests.
- Test execution required sandbox escalation for local VSTest/testhost communication. Initial sandboxed runs timed out before connecting to testhost and were cancelled; these were environment failures, not failing assertions.
- SQL Server and Redis integration connection environment variables were not configured. Do not describe skipped external suites as verified.
- Existing tests already cover persisted detail with no SaveChanges/provider calls, unrelated Traveler denial, missing itinerary, historical ID resolution, closed-group denial, and public POI behavior. No synthetic backend endpoint, schema migration or production logic was added.
- Added characterization cases to GetItineraryDetailEndpointTests: an active group member reads the current resolved version with CanManage=false; left/removed members are forbidden. Focused dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore -m:1 --disable-build-servers --filter FullyQualifiedName~GetItineraryDetailEndpointTests: passed all 7 cases, zero skips/failures.
- dotnet format whitespace tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --include tests/TripMate.Api.IntegrationTests/Itineraries/GetItineraryDetailEndpointTests.cs --verify-no-changes --verbosity quiet: passed after normalizing the final newline.
- Full pre-edit baseline API suite: 999 passed, 811 skipped, zero failures (8m28s). Infrastructure: 1,316 passed, 3 skipped; CandidatePoolReplay: 13 passed. The full Application run was interrupted during unrelated long-running Golden benchmarks. A complete solution-wide test pass and skipped SQL/Redis suites are not claimed.
- Independent review: parent reviewed the new test for spec compliance and code quality with no material findings.
- Live local DB acceptance on 2026-11-23: user authorized backup and existing-migration synchronization. COPY_ONLY/CHECKSUM backup verified successfully and copied to host. Ledger dry-run identified 8 missing migrations and one changed role migration, reviewed and reapplied through the existing runner. TM-95 stopped on a legacy nullable nvarchar(24) lock-status column; verified the column had no non-null values/dependent checks/indexes, converted it to the required varchar(24), reconciled the rolled-back intent and resumed successfully. All current migration checksums then matched. Existing business table row counts were preserved (ledger 80 -> 88).
- Restarted exact UC16 backend: public tour search and authenticated itinerary 41420 returned 200. Production Mobile downloaded 31.1 MB, survived a process restart with airplane mode on and Wi-Fi/data off, and displayed cached itinerary/POI/map/planned route. No new UC16 schema migration or API endpoint was added.
