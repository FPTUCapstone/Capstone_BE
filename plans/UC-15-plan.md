# UC-15 Confirm Re-routing Backend Implementation Plan

Status: **DRAFT — awaiting developer approval (2026-11-24).** Spec `specs/UC-15-confirm-rerouting-spec.md` is approved with decisions D1-D6 (2026-11-24).
Branch: `feature/hoangtv-uc15-confirm-rerouting`, worktree `_wt/Capstone_BE_uc15`, based on `origin/develop` fdbc1e20.
Tasks are sequential and independently testable. Each task is TDD (failing test first), ends green, and gets a review pass against the spec before the next starts. A Critical finding blocks the next task.

## Ground rules for every task

- No EF migrations and no SQL migration (spec 3.3). If a task finds it needs one, stop and ask.
- `*.cs` files have **no final newline** (`.editorconfig`); strip the newline the Write tool adds. Scheduling golden tests rewrite five tracked JSON files on every run: `git restore tests/**/Scheduling/Golden` afterwards.
- Do not touch `Features/Scheduling/Common|Csp|Routing`. If a task would, stop: it triggers the solver-comparison report rule.
- Business rules not in the approved spec are not invented: stop, ask, document (AGENTS 1.1).
- No commit, push or PR unless the developer asks. Proposal: one local commit per finished task once approved.
- Constants, not magic strings, for statuses/strategies/error codes (AGENTS 4.1). Result pattern, no business exceptions.

## Commands

| Name | Command (from the worktree root) |
| --- | --- |
| Build | `dotnet build TripMate.slnx -c Release` |
| Application unit | `dotnet test tests/TripMate.Application.UnitTests -c Release` |
| Infrastructure unit | `dotnet test tests/TripMate.Infrastructure.UnitTests -c Release` |
| API non-SQL integration | `dotnet test tests/TripMate.Api.IntegrationTests -c Release --filter "Category!=SqlServer&Category!=Redis"` |
| SQL Server integration | set `TRIPMATE_SQLSERVER_TEST_CONNECTION` to the local test instance (never printed or committed), then `dotnet test tests/TripMate.Api.IntegrationTests -c Release --filter "Category=SqlServer"`. Without the variable these tests **skip silently**, so every task that lists them must confirm the run shows them as *passed*, not skipped. |

**Baseline gate (before T1):** run Build + the three suites above on the clean worktree and record the pass counts in the T1 report. Do not start on a red baseline.

SQL Server tests matter here because the unit-test `TestDbContext` does not enforce foreign keys or unique indexes; the orderings in T7 can only be proven on real SQL Server.

---

## T1 — Options, error codes, snapshot contract

Goal: configuration and the shared contract types that later tasks depend on. No behaviour yet.

Files (new unless noted), `src/TripMate.Application/Features/Rerouting/Common/`:
- `ReroutingOptions.cs`: section `Rerouting`; `ProposalTtl` (15 min, valid 1-60), `MaxVariants` (8), `MaxReplacementsPerStop` (3), `ProposalRetryWindow` (30 min, valid 1-240; plan-level addition for the retry rule in T5, flagged for approval) + `ReroutingOptionsValidator` (fails start-up on invalid values, same style as `SchedulingGenerationOptionsValidator`).
- `ReroutingErrorCodes.cs`: `rerouting.not_found`, `rerouting.proposal_expired`, `rerouting.proposal_closed`, `rerouting.proposal_infeasible`.
- `ReroutingProposalSnapshot.cs`: records for schema v1 (spec 3.2), `ReroutingStrategies` constants, `Serialize` / `TryParse` (returns null for demo rows without `schemaVersion`, unknown version, or malformed JSON; camelCase, UTC strings).
- `ReroutingProposalDetailResponse.cs`: the GET DTO (spec 5).
- `src/TripMate.Application/DependencyInjection.cs` (edit): bind and validate `ReroutingOptions`.
- `src/TripMate.Api/appsettings.json` (edit): `Rerouting` section with the defaults.

Tests first (`tests/TripMate.Application.UnitTests/Features/Rerouting/`): `ReroutingOptionsValidatorTests` (each bound, defaults), `ReroutingProposalSnapshotTests` (round trip, demo row `{"demo":true}` -> null, unknown `schemaVersion`, missing arrays).

Verify: Application unit (filter `Rerouting`), Build.
DoD: options validated at start-up; snapshot round-trips; nothing else changed; baseline counts unchanged.

## T2 — Domain: proposal transitions and session rebind primitives

Goal: pure domain, no EF/HTTP. Primitives are split so T7 can order the saves (see T7 table).

Files:
- `src/TripMate.Domain/Entities/ReroutingEvent.cs` (edit): static `Propose(Incident incident, TripSession session, string snapshotJson, DateTimeOffset proposedAtUtc)` (assigns navigation properties, UTC-only, non-empty snapshot); `Accept(now)`, `Reject(now)`, `Expire(now)` each return a `ReroutingTransition` (`Applied | Replayed | Closed`): `Proposed` -> target = `Applied` and sets `decided_at`; same target again = `Replayed`; any other terminal state = `Closed`. `IsExpiredAt(now, ttl)` = `now > ProposedAtUtc + ttl` (strictly greater).
- `src/TripMate.Domain/Entities/TripSession.cs` (edit):
  - `RemovePendingItems()`: removes every `Pending` snapshot row from `_items` and returns them (does nothing on a terminal session).
  - `AttachRerouteItems(long newItineraryId, IReadOnlyList<TripSessionItem> newItems, DateTimeOffset now)`: requires no Pending row left, sequences continue after the highest kept sequence, sets `ItineraryId`, adds the rows, records a `TripStateHistory` row (`reason = rerouting_accepted`, `triggered_by = Traveler`, from = to = current state; no FSM change). `RequestedItineraryId`, `FsmState`, `ExploringItemId` untouched.
- `src/TripMate.Domain/Entities/TripStateHistory.cs` (edit): `RerouteAcceptedReason` constant.
- `src/TripMate.Domain/Entities/Incident.cs` (edit): `DetachAffectedItem()` (sets `AffectedItineraryItemId = null`) and `RepointAffectedItem(long itineraryItemId)`; both only valid on an already resolved incident (guards T7 misuse).

Tests first (`tests/TripMate.Application.UnitTests/Domain/` or the existing domain-test location; follow where `TripSession` tests live): every transition and replay/closed combination; TTL boundary (`+15:00` valid, `+15:00.001` expired); `RemovePendingItems` keeps Reached/Skipped; `AttachRerouteItems` sequences, history row, state unchanged, rejects while Pending rows remain; Incident guards.

Verify: Application unit (domain filter), Build.
DoD: all spec 3.1 transitions covered; no infrastructure types referenced from Domain.

## T3 — Evaluator anchor and rerouted-version builder

Goal: spec 2.1. Backward compatible; existing callers and tests untouched.

Files:
- `src/TripMate.Application/Features/Itineraries/Common/EvaluationAnchor.cs` (new): record from spec 2.1.
- `src/TripMate.Application/Features/Itineraries/Common/IItineraryVersionService.cs` (edit): `EvaluateStopVariantsAsync(..., EvaluationAnchor? anchor = null)` and new `CreateReroutedVersionAsync(Itinerary source, SchedulingRequest request, IReadOnlyList<ItineraryItem> keptItems, GeneratedItineraryPlan remainingPlan, DateTimeOffset now, CancellationToken)` that builds the successor (`Version + 1`, same series, items = copies of `keptItems` in order then the plan items with continuing sequence, provenance `reroute`, `Accept(now)`) and adds it to the context without saving.
- `src/TripMate.Application/Features/Itineraries/Common/ItineraryVersionService.cs` (edit): with an anchor, build `GenerationInput` from the anchor (start time, start point, available minutes, remaining budget); load POIs around `anchor.CandidateCenter` within `CandidateRadiusKm` **and** every POI used by a variant (a variant POI is never lost to a radius filter); no `MaxAvailableMinutes` relaxation when an anchor is given (infeasible if it cannot end by the anchor's day end even with full compression).
- `src/TripMate.Application/Features/Scheduling/Common/SchedulingProvenance.cs`: **do not edit** (Scheduling/Common is frozen). Add `RerouteProvenance` in `Itineraries/Common` that sets `solver_outcome = "reroute"` via `itinerary.RecordGenerationProvenance`.
- Reuse `GeneratedItineraryItemMapper` for plan items; copy kept items through a new `ItineraryItem` copy factory only if no public path exists (check `ItineraryItem.CreateVisit/CreateRest` first; add `ItineraryItem.CopyForVersion(...)` in Domain if needed, preserving kind, times, stay, travel-to-next, cost, reasons, mandatory flag, and status Visited/Skipped).

Tests first (`tests/TripMate.Application.UnitTests/Features/Itineraries/`): evaluation without anchor equals today's result (reuse an existing fixture, assert identical plan); with anchor starts at anchor time/point and the plan's first arrival is >= anchor start; a variant POI outside the trip radius but inside the anchor radius is evaluated (not "unavailable"); remaining budget exceeded -> `ExceedsBudget`; cannot end by day end -> infeasible even though manual adjust would relax; `CreateReroutedVersionAsync` keeps reached items, continues sequence, `Version + 1`, Active, provenance `reroute`, old version unchanged.

Verify: Application unit (`Itineraries` + `Scheduling` filters, to prove no regression), Build; confirm `git diff --stat` shows nothing under `Scheduling/`.
DoD: all pre-existing Itineraries and Scheduling tests still pass without edits; golden files restored.

## T4 — Rerouting Engine (strategies and proposal builder)

Goal: given a session and an incident, return the best feasible proposal or nothing. No persistence of proposals yet.

Files (`src/TripMate.Application/Features/Rerouting/Engine/`, new):
- `ReroutingAnchorFactory.cs`: spec 4.2 (origin = last Reached/Exploring stop else request start; start = `max(now, exploring item's planned departure)`; end = original day end; remaining budget; radius from `dbo.SystemConfigs` `Rerouting.SearchRadiusKm` via `AlgorithmParameterDefinitions.ParseSearchRadius`).
- `ReroutingCandidateFinder.cs`: replacement POIs (Active, `PlanningPoiEligibility.IsPlanningReady`, not in itinerary and not reached/skipped in session, within radius of the affected stop, ordering and `MaxReplacementsPerStop` from spec 4.3; Indoor candidates require `IndoorOutdoorReviewedAtUtc != null`).
- `Strategies/` one class per row of the spec 4.3 table: `SevereWeatherStrategy`, `PoiClosureStrategy`, `ScheduleDelayStrategy`, `RouteDeviationStrategy`, each producing ordered `ItineraryStop` variants with a `ReroutingStrategies` label; mandatory stops never dropped/replaced (closure may postpone).
- `ReroutingProposalBuilder.cs`: evaluates variants through `EvaluateStopVariantsAsync(..., anchor)`, picks the winner (first feasible by table order; within a strategy smallest `arrivalTimeDiffMinutes`, then `travelTimeDiffMinutes`), drops a variant identical to the current plan, computes diffs and `Kept/Added/Moved` markers, returns `ReroutingProposalSnapshot` or `null`.

Tests first (`tests/TripMate.Application.UnitTests/Features/Rerouting/Engine/`): for each strategy a feasible case, an infeasible case (-> null), a mandatory-stop case, an identical-to-current case (RouteDeviation with unchanged order -> null), and the closure postpone case; anchor factory cases (navigating, exploring with later planned departure, no reached stop, budget deduction, radius from config and default); deterministic tie-breaking; `MaxVariants` bound respected.

Verify: Application unit (filter `Rerouting`), Build.
DoD: engine is pure of side effects (no `SaveChanges`); every spec 4.3 row has a test; BR-25 covered (a variant failing opening hours is never returned).

## T5 — Triggers, one-open-proposal rule, sweeper, alert-list TTL filter

Goal: proposals appear automatically; alerts are never delayed or failed by the engine.

Files:
- `src/TripMate.Application/Features/Rerouting/Generate/ReroutingProposalGenerator.cs` (new): `TryGenerateAsync(sessionId, incidentId, ct)` implementing spec 4.1 rules 1-5 (trigger types, Pending affected item, open session, no existing event for the incident, at most one unexpired `Proposed` per session, insert `ReroutingEvent.Propose` only for a built proposal). Wraps everything in try/catch with structured logging and never rethrows (rule 4). Uses `ReroutingEvent` insert in its own save.
- `src/TripMate.Application/Features/TripAlerts/Scanning/TripAlertScanner.cs` (edit): after a created incident (inside `EnsurePersisted`, after the incident/notification transaction has committed) call the generator; new scan step `RetryProposalsAsync` (open trigger-type incidents detected within `ProposalRetryWindow`, no event yet, session has no open proposal, capped at 20 per scan) and `ExpireOverdueProposalsAsync` (marks `Proposed` events past TTL, or whose session ended, as `Expired`).
- `src/TripMate.Application/Features/TripAlerts/ReportDeviation/ReportDeviationCommandHandler.cs` (edit): call the generator after the deviation incident is created (not on replay, not on the "already has an open deviation" path).
- `src/TripMate.Application/Features/TripAlerts/List/ListTripAlertsQueryHandler.cs` (edit): return `reroutingProposalId` only when the newest `Proposed` event is not past its TTL (spec 8). Inject `IOptions<ReroutingOptions>` and `IDateTimeProvider`.
- `src/TripMate.Application/DependencyInjection.cs` (edit): register the generator and engine services.

Tests first: generator unit tests (each trigger type creates exactly one event; `WeatherAdvisory`, overstay delay on the exploring stop, non-Pending affected item, ended/expired session, existing event for the incident, another open proposal in the session -> none; engine returns null -> none; engine throws -> no exception escapes and the incident/alert remains); scanner tests (retry within window only, capped, expiry sweep incl. ended session); list handler test (proposal hidden once past TTL, boundary); deviation handler replay does not generate.

Verify: Application unit (filters `Rerouting` and `TripAlerts`), API non-SQL integration `TripAlerts` filter, Build.
DoD: existing UC-14 tests pass untouched; an engine failure cannot change alert creation or response.

## T6 — API: GET, accept, reject (thin layer)

Goal: HTTP contract, validation and error mapping. Handlers for accept/reject are stubs returning the typed failure until T7 fills them, so this task is testable on routing/auth/validation alone. GET is complete here.

Files:
- `src/TripMate.Application/Features/Rerouting/Get/GetReroutingProposalQuery.cs` + `GetReroutingProposalQueryHandler.cs` + validator: ownership (session owner, else `not_found` 404 for proposal/session mismatch and foreign user), effective status (`Proposed` past TTL -> `Expired`, no write), builds `ReroutingProposalDetailResponse` from the snapshot (demo/invalid snapshot -> `not_found`).
- `src/TripMate.Application/Features/Rerouting/Accept/AcceptReroutingProposalCommand.cs`, `.../Reject/RejectReroutingProposalCommand.cs` (+ validators, `TravelerUserId`, `SessionId`, `ProposalId`); handlers added in T7.
- `src/TripMate.Api/Controllers/V1/NavigationSessionsController.cs` (edit): `GET {sessionId:long}/rerouting-proposals/{proposalId:long}`, `PUT .../acceptance` (200 `NavigationSessionResponse`), `PUT .../rejection` (204); `[ProducesResponseType]` for 200/204/400/401/403/404/409/500 like the UC-14 actions; Traveler role.
- Error mapping: `ReroutingErrorCodes` -> 404/409/409/409 in the existing `HandleFailure` map (find the registry used for `TripAlertErrorCodes`/`NavigationErrorCodes`); 500 stays the generic mapping.
- OpenAPI: update the snapshot/test that covers navigation OpenAPI (`NavigationSessionOpenApiTests`) and any schema filter requirements of AGENTS 4.1.

Tests first: endpoint tests (anonymous 401, wrong role 403, foreign user/session mismatch 404, bad ids 400, GET happy path with a seeded valid snapshot, GET effective-status `Expired` without a DB write, demo row 404); OpenAPI test lists the three routes and the new schemas; error-code-to-status mapping test.

Verify: API non-SQL integration (`TripAlerts`/`Navigation` filters), Application unit, Build.
DoD: the GET works end to end; accept/reject routes exist and are covered for auth/validation; contract matches spec section 5 exactly.

## T7 — Accept and Reject pipelines (transaction, expiry, double tap, FK-safe saves)

Goal: spec sections 6 and 7. This is the risk task: keep it small, reviewed twice.

Files:
- `src/TripMate.Application/Features/Rerouting/Accept/AcceptReroutingProposalCommandHandler.cs` (new, fills T6 stub). Structure follows `NavigationSessionMutator`: retry loop (3 attempts) around `ExecuteInSerializableTransactionAsync`; on `DbUpdateConcurrencyException` or `dbContext.IsDeadlock` call `ClearTrackedEntities()` and re-run the whole attempt, authorization included. Inside: `IItineraryMutationLock` (resource via `ItineraryMutationLockResource.ResolveAsync`, as `AdjustItineraryItemsCommandHandler`), then spec section 6 steps 1-13.
- `.../Reject/RejectReroutingProposalCommandHandler.cs` (new): spec section 7 (same lazy expiry, replay, closed handling; no itinerary or session writes).
- `src/TripMate.Application/Features/Rerouting/Accept/RerouteApplier.cs` (new, internal): the save-ordered application of steps 8-11, kept separate so the ordering is testable in one place.
- `src/TripMate.Application/Features/Navigation/Common/ActiveRouteNavigationProjection.cs`: reuse `EnrichAsync` only; no edit expected.

**Transaction facts verified in the code (must be respected):** `ApplicationDbContext.ExecuteInTransactionAsync` commits whenever the delegate returns, even when it returns a failed `Result`, and rolls back only on an exception. Therefore (a) the *expiry-and-return-409* paths intentionally write `Expire(now)` and commit; (b) every other failure path must return **before any write**, and a failure after writes must throw (so everything rolls back) or be converted by the handler into a throw inside the delegate and mapped to MSG127 outside it. Add a test per failure path that proves "no partial writes".

**Constraints that dictate the save order (SQL Server):**

| Constraint | Source | Effect |
| --- | --- | --- |
| `UQ_TripSessionItems_Sequence` unique `(session_id, sequence_no)` | `TripSessionItemConfiguration` | New rows continue the sequence after the kept rows, i.e. they reuse exactly the numbers the old Pending rows hold. The old rows must be **deleted before** the new rows are inserted. |
| `FK_Incidents (session_id, affected_itinerary_item_id) -> TripSessionItems`, `NoAction`, nullable | migration `20261022_add_uc14_alert_contract.sql`, `IncidentConfiguration` | A Pending row cannot be deleted while any incident (resolved or not) still points at it. Those pointers must be **set to NULL before** the delete. |
| `TripSessionItems.itinerary_item_id -> ItineraryItems`, `Restrict` | `TripSessionItemConfiguration` | The successor itinerary and its items must be **saved before** the new session rows are inserted (their ids are needed). |
| Incident repoint to the new row | spec 6 step 10 | Only possible **after** the new rows exist. |
| `TripSessions.RowVersion` | `MarkTripSessionProgressForConcurrencyCheck` | Fence first, so a concurrent reach/skip is detected before any other write. |

Because EF Core does not promise delete-before-insert ordering across entity types inside one `SaveChangesAsync`, the applier uses **five explicit saves inside the one transaction**:

1. **S1** add successor itinerary + items (`CreateReroutedVersionAsync`) and `MarkTripSessionProgressForConcurrencyCheck(session)`; save. (Version ids now known; a concurrent session change throws here, before any destructive write; whole attempt retries.)
2. **S2** for every incident bound to a Pending row about to be replaced: `Resolve(now)` and `DetachAffectedItem()` (pointer set to NULL), remembering `(incidentId, poiId)`; save.
3. **S3** `session.RemovePendingItems()` (DELETE of the old Pending rows); save.
4. **S4** `session.AttachRerouteItems(successor.Id, newItems, now)` (INSERT new rows, `ItineraryId` update, history row); expire sibling `Proposed` events; `proposal.Accept(now)`; save.
5. **S5** for each remembered `(incidentId, poiId)` whose POI exists in the new rows: `RepointAffectedItem(newItemId)`; save. POIs that left the plan stay NULL.

Any exception after S1 rolls the whole transaction back (old version, old rows and incident pointers are restored); only the retry loop re-runs it.

Tests first:
- Unit (Application, handler logic with test doubles): replay `Accepted` (200, no writes); `Rejected`/`Expired` -> 409; lazy expiry at `+15:00.001` writes `Expired` and returns 409 while `+15:00` proceeds; stale base stop (reached/skipped meanwhile) -> expired + 409; terminal session -> expired + 409; infeasible at accept -> expired + 409; foreign user/mismatch -> 404; siblings expired; incident resolved; reject paths (success, replay, closed, expired).
- **SQL Server integration** (`[SqlServerFact]`, `Category=SqlServer`, `SqlServerTestDatabase.CreateAsync()`), in `tests/TripMate.Api.IntegrationTests/Rerouting/ReroutingAcceptSqlServerTests.cs`:
  - accept with an incident pointing at a Pending row whose sequence number is reused by a new row: no FK or unique-index error, exactly one `Version + 1` itinerary, old version rows intact, Reached rows untouched, Pending rows replaced with continuing sequences, triggering incident resolved, pointer repointed (POI kept) or NULL (POI dropped), sibling proposal `Expired`, history row written;
  - **parallel double accept** (two concurrent requests): both 200, one new version, one set of new rows;
  - accept after reject -> 409, after TTL -> 409, both with no change to itinerary or session;
  - **fault injection after S3** (throw from a test hook/decorated context) -> everything rolled back: old rows, incident pointers and old version unchanged, proposal still `Proposed`, API 500 (MSG127);
  - existing Admin Active Trip Details test still passes with a rerouted session.

Verify: Application unit (`Rerouting` filter), SQL Server integration (filter `Rerouting`, then the whole `Category=SqlServer` set; confirm *passed*, not skipped), API non-SQL integration, Build.
DoD: spec 2.2 and 2.3 satisfied with tests; the five-step order is asserted by the SQL tests above; no code path writes before a failure return except the intentional expiry commits.

## T8 — Full verification, UAT seed, handoff

Files:
- `database/seeds/` new idempotent seed (follow `20261001_seed_uc59_active_trip_details.sql` style, `README.md` entry): a Navigating session with a valid **snapshot v1** `Proposed` event (TTL still open when seeded relative to `SYSUTCDATETIME()`), plus an expired and a rejected one. Seeds are never run against a local database as verification: Azure verification rules apply and need the developer's go before touching Azure.
- `docs/` or the handoff file: see below.
- `ReroutingEvent` demo rows from the UC-59 seed keep their `{"demo":true}` shape; confirm they return `404 rerouting.not_found` and still render in Admin Active Trip Details.
- Final review pass of the whole diff against the spec (section by section checklist) and `/code-review`-style self review for Critical/High.

Verify (all must be green): `dotnet build TripMate.slnx -c Release`; Application unit; Infrastructure unit; `Category=SqlServer` (passed, not skipped); `Category!=SqlServer&Category!=Redis`; `git status` shows no stray golden JSON; `git diff origin/develop --stat` shows nothing under `Features/Scheduling/{Common,Csp,Routing}` and no files under `database/migrations`.

Handoff document (deliverable requested by the developer): `D:\Capstone_Project_Production\Handoff\UC-15-confirm-rerouting-be-handoff.md` containing what UC-15 is, what was required (spec summary and decisions D1-D6), what was done per task with test counts, what is not done (Mobile wiring, real GPS, Azure UAT seed), risks, and the exact verification commands and results.

DoD: every spec section has a passing test or a documented reason; handoff written; developer asked the merge / PR / keep / discard question (AGENTS 1.2.6) — no merge or push without their answer. No migration exists, so there is **no Azure migration gate** for merging this branch (develop auto-deploys to Railway); the Mobile follow-up must wait for the BE PR to be merged.

---

## Order and dependencies

T1 -> T2 -> T3 -> T4 -> T5 -> T6 -> T7 -> T8. T2 and T3 do not depend on each other and may be swapped. T6 before T7 on purpose: the HTTP contract and error mapping are reviewed before the risky pipeline is filled in.

## Open points for the developer (plan level)

- P1 `ProposalRetryWindow` option (30 min) bounds the retry rule of spec 4.1 so an infeasible incident is not re-evaluated every 10 minutes forever. Spec text says "retried on later scans"; this is the smallest bound that keeps that behaviour finite.
- P2 One local commit per finished task, when you say so.
- P3 `CopyForVersion` may be added to `ItineraryItem` if no existing public path copies an item with Visited/Skipped status (decided in T3 after reading the entity factories).
