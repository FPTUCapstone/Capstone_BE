# UC-15 Confirm Re-routing — Backend spec (Rerouting Engine + confirmation)

Status: **DRAFT — awaiting developer approval (2026-11-24).** No code is written before approval.
Branch: `feature/hoangtv-uc15-confirm-rerouting` (worktree `_wt/Capstone_BE_uc15`, based on `origin/develop` fdbc1e20).
Authority: Report 3 SRS section 3.3.6 (BR-25, BR-26, BR-27, BR-31, BR-33, PC-01..PC-04, MSG41-MSG44, MSG127); UC-13 spec line 78 and 541; UC-14 spec ("UC-14 exposes an existing UC-15 proposal ... does not generate, apply or mutate an itinerary").
Platform: Traveler on **mobile only**. This spec covers Backend. Mobile wiring is a separate follow-up.

## 1. Scope

In scope (decided by the developer on 2026-11-24, approach 1 "rule-based candidates validated by the existing evaluator"):

1. **Rerouting Engine**: when an alert raises an `Incident`, generate a rerouting proposal (an alternative plan for the *remaining* part of the trip) and store it in `trip.ReroutingEvents` as `Proposed`.
2. **Confirmation**: Traveler reads a proposal, accepts it, or keeps the current route. An unanswered proposal expires 15 minutes after `proposed_at`.
3. **Application of an accepted proposal**: a new itinerary version (previous version retained), the open navigation session moves onto it, reached/skipped history is kept.

Out of scope / non-goals:

- Mobile/FE changes, push copy, Admin UI. The Admin Active Trip Details page already lists `ReroutingEvents` (UC-58) and keeps working.
- Real map geometry for an alternative road route (the integrated map service owns geometry; BE only orders and times stops).
- GPS or live traveler position. **No Backend code stores the traveler position** (`TripSession.CurrentLatitude/Longitude` and `trip.TripLocationLogs` are never written; UC-14 deviation reports carry no coordinates). The engine therefore anchors on stop data only (section 4.2).
- Proposals for `WeatherAdvisory` (Info) incidents, overstay alerts on the *exploring* stop, or incidents with no Pending affected stop.
- Schema change: **none** (section 3.3). No Azure migration is needed for this task.
- Anti-goal: an itinerary must never change without an explicit Accept (BR-33, PC-04). Generation, scanning, GET, reject and expiry never touch `planning.Itineraries` or `TripSessionItems`.

## 2. Verification of the three open points

### 2.1 Does `EvaluateStopVariantsAsync` evaluate the *remaining* route from the current position and time?

**No, not as written.** Verified in `ItineraryVersionService.EvaluateStopVariantsAsync` / `EvaluateStopsAsync` (Itineraries/Common):

| Aspect | Current behaviour | Consequence for rerouting |
| --- | --- | --- |
| Start time | `request.StartAtUtc` (the original day start) | A plan is timed from the morning, not from now. |
| Start point | `request.StartLatitude/Longitude` | Not the last stop the traveler really reached. |
| Time budget | `request.AvailableMinutes` from `StartAtUtc` | Day end is only right if start is unchanged. |
| Candidate pool | POIs within `request.SearchRadiusKm` of `request.ExplorationLatitude/Longitude` (both `LoadRelevantActivePoisAsync` and the in-memory radius filter; `keepIds` skips the *count* limit but not the radius filter) | A replacement POI that is inside `Rerouting.SearchRadiusKm` of the affected stop but outside the original trip radius is dropped and reported as unavailable. |
| Budget | `request.BudgetVnd` against the whole plan | Money already spent on reached stops is not deducted. |
| Reached/skipped stops | Not represented; the variant is the whole day | The caller must pass only the remaining stops, and time/budget must be reduced accordingly. |
| Persisting | `CreateAdjustedVersionAsync` writes a *whole-day* version from `request.StartAtUtc` | Cannot keep history of reached stops. |

What it *does* provide and we reuse: opening-hours check at planned arrival (`ClosedAtArrival`), `ExceedsEndTime`, `ExceedsBudget`, route matrix through `IRouteDurationProvider` (memoised), elastic visit compression, fixed-order scheduling (`ItineraryGenerationService.GenerateFixedOrderAsync`, whose `GenerationInput` is already parameterised by `StartAtUtc`, `Start`, `End`, `AvailableMinutes`, `BudgetVnd`).

**Required change (task T3, backward compatible):** add an optional `EvaluationAnchor` argument to `EvaluateStopVariantsAsync`:

```csharp
public sealed record EvaluationAnchor(
    DateTimeOffset StartAtUtc,          // max(now, planned departure of the stop being explored)
    RoutePoint Start,                   // last Reached/Exploring stop, else request start
    int AvailableMinutes,               // original day end minus StartAtUtc, never above MaxAvailableMinutes
    decimal? RemainingBudgetVnd,        // request budget minus estimated cost of Reached stops (null = no budget)
    decimal CandidateRadiusKm,          // Rerouting.SearchRadiusKm
    RoutePoint CandidateCenter);        // the affected stop
```

When `anchor` is null the method behaves exactly as today (all existing callers and tests unchanged). When present it replaces `StartAtUtc`, `Start`, `AvailableMinutes`, `BudgetVnd` in `GenerationInput`, and loads/filters candidates by `CandidateCenter` and `CandidateRadiusKm` **plus** every POI in the variants (so a replacement is never lost to a radius filter). The change stays in `Features/Itineraries/Common`; it does not touch `Scheduling/Common|Csp|Routing`, so no solver comparison report regeneration is required (UC-10 gotcha). The compression tiers keep working; a variant is infeasible if even full compression ends after the original day end (no `MaxAvailableMinutes` relaxation for rerouting, unlike manual adjust).

A separate new method on the version service creates the rerouted version (section 6). `CreateAdjustedVersionAsync` is not reused for persisting.

### 2.2 Accept checks expiry itself

Expiry is evaluated **at the moment of the call**, not only by the scan job. Rule: expired when `now > proposed_at + ProposalTtl` (strictly greater; exactly at the boundary is still valid). The same check runs in Accept, Reject and GET (GET reports effective status `Expired` without writing). The sweeper in the scan job is only housekeeping so that the list/alert data and Admin view become consistent; correctness never depends on it. See section 6, step 5.

### 2.3 Accept is one transaction and safe against double tap

Accept (section 6) runs in a single `ExecuteInSerializableTransactionAsync` transaction holding `IItineraryMutationLock` for the trip series, with the `TripSession` row-version fence (same retry-on-concurrency/deadlock loop, 3 attempts, as `NavigationSessionMutator`). Version creation, `TripSessionItems` rewrite, incident resolution, sibling-proposal expiry and the status change commit together or not at all. Double tap / retry / two devices: the second request serialises behind the first, finds status `Accepted` and **replays the success response** without creating another version (PC-01: exactly one new version). Idempotency is state-based on the natural key `proposalId`; no `Idempotency-Key` header and no `ItineraryVersionOperation` row (its `operation_type` CHECK would need a migration).

## 3. Data contract

### 3.1 Proposal states

`trip.ReroutingEvents.status` stays `Proposed | Accepted | Rejected | Expired` (`Rejected` = SRS "declined"). Transitions (domain methods on `ReroutingEvent`, no public setters):

`Proposed -> Accepted | Rejected | Expired`. All other transitions are rejected. `decided_at` is set on every transition out of `Proposed`.

### 3.2 `proposed_itinerary_snapshot` JSON (schema version 1)

Stored as the existing `NVARCHAR(MAX)` column; UAT demo rows (`{"demo":true,...}`) have no `schemaVersion` and are treated as "no valid proposal" (never accepted).

```json
{
  "schemaVersion": 1,
  "strategy": "ReplaceWithIndoor | ReplaceSameCategory | Drop | Postpone | ReduceStops | Reorder",
  "reasonCode": "trip_alert.severe_weather",
  "baseItineraryId": 100,
  "baseItineraryVersion": 2,
  "anchor": { "startAtUtc": "...", "latitude": 16.05, "longitude": 108.2, "availableMinutes": 300 },
  "baseStops":     [ { "itineraryItemId": 11, "poiId": 5, "name": "...", "latitude": 0, "longitude": 0,
                       "plannedArrivalUtc": "...", "plannedDepartureUtc": "...", "isMandatory": false } ],
  "proposedStops": [ { "poiId": 9, "name": "...", "latitude": 0, "longitude": 0,
                       "plannedArrivalUtc": "...", "plannedDepartureUtc": "...", "isMandatory": false,
                       "transportModeToNext": null, "change": "Kept|Added|Moved" } ],
  "removedPoiIds": [5],
  "affectedItineraryItemIds": [11],
  "travelTimeDiffMinutes": -12,
  "arrivalTimeDiffMinutes": 20,
  "endAtUtc": "..."
}
```

`baseStops` are the Pending stops at generation time (their `itineraryItemId` values are the staleness fence, section 6 step 6). The differences are computed as `proposed - base` over the remaining plan: total travel minutes, and arrival time of the final remaining stop. Display-only extras (cost, distance) are not part of the contract (Mobile treats them as optional).

### 3.3 Configuration and schema

- **No migration.** `ReroutingEvents` already holds incident, session, snapshot, status, `proposed_at`, `decided_at`; the TTL is computed. `ItineraryItems`/`Itineraries` already carry what a rerouted version needs; `Itineraries.solver_outcome` is a free `VARCHAR(64)` (new provenance value `reroute`, no CHECK).
- `ReroutingOptions` (section `Rerouting` in appsettings, validated on start): `ProposalTtl` default `00:15:00` (range 1-60 min), `MaxVariants` default 8, `MaxReplacementsPerStop` default 3.
- Search radius: `dbo.SystemConfigs` key `Rerouting.SearchRadiusKm` via `AlgorithmParameterDefinitions.ParseSearchRadius` (default 5 km, 1-50 km, already managed by UC-57). Read per generation, so an Admin change applies to the next proposal.

## 4. Rerouting Engine

### 4.1 When it runs

`ReroutingProposalGenerator` (Application) is invoked right after an Incident is persisted, from the two places that create trip-alert incidents:

- `TripAlertScanner` (`SevereWeather`, `POIClosure`, `ScheduleDelay` on a Pending item),
- `ReportDeviationCommandHandler` (`RouteDeviation`).

Rules:

1. Trigger types: `SevereWeather`, `POIClosure`, `ScheduleDelay`, `RouteDeviation` only. Not `WeatherAdvisory`; not the overstay `ScheduleDelay` raised for the exploring stop (`source_key` prefix `TripAlertResponse.OverstaySourcePrefix`).
2. The affected item must exist and be `Pending`, and the session must be open (`Navigating` or `Exploring`, not expired).
3. One proposal per incident. At most **one `Proposed` (unexpired) proposal per session**; while one is open, new incidents get no proposal (the alert is still delivered). Because the scanner re-checks every scan interval, an incident that still has no proposal is retried on later scans after the open one is closed or expired (bounded by the existing `UQ_Incidents_OpenSource` dedupe, plus a check "no ReroutingEvent for this incident in any status").
4. Generation failure never fails or delays the alert: it runs after the incident/notification commit, in its own try/catch with structured logging; the persisted alert list stays authoritative. No feasible variant means no `ReroutingEvent` (SRS abnormal 3.a1/4.a1: "discarded and not offered"), so `reroutingProposalId` stays null.
5. Insert the `ReroutingEvent` only after a variant passed evaluation (BR-25). The first/best variant is stored; the Traveler sees one proposal, not a menu.

### 4.2 Anchor (no GPS)

- Remaining stops R = session items in `Pending` status ordered by sequence.
- Origin = coordinates of the last `Reached` item (the one being explored if `Exploring`); if none reached, the request start point.
- Start time = `max(now, PlannedDepartureUtc of the exploring item)`; if `Navigating`, `now`.
- End = original day end (`request.StartAtUtc + AvailableMinutes`, then the request end POI if any).
- Remaining budget = request budget minus summed `EstimatedCost` of items already `Reached` (null when no budget).

### 4.3 Candidate strategies

All strategies keep `IsMandatory` stops in the plan: a mandatory stop is never dropped or replaced (mirrors the "Mandatory locations cannot be removed" rule of the planner). Replacement candidates must be Active, planning-ready (`PlanningPoiEligibility.IsPlanningReady`), not already in the itinerary or reached/skipped in this session, within `Rerouting.SearchRadiusKm` of the affected stop, and are checked for opening hours by the evaluator at their new arrival time. Candidates are ordered by (same category first, then distance to the affected stop, then `TravelerPreferenceScoring`), bounded by `MaxReplacementsPerStop`.

| Incident | Variants generated (in priority order) |
| --- | --- |
| `SevereWeather` on an Outdoor, non-mandatory stop A | (1) replace A by an Indoor POI with `IndoorOutdoorReviewedAtUtc != null` (`ReplaceWithIndoor`); (2) drop A (`Drop`). |
| `POIClosure` on stop A | (1) replace A by a same-category POI (`ReplaceSameCategory`); (2) move A to the first later slot where the evaluator says it is open (`Postpone`; allowed for mandatory A); (3) drop A if non-mandatory (`Drop`). |
| `ScheduleDelay` on the next Pending stop | `ReduceStops`: drop each non-mandatory Pending stop in turn, lowest preference score first, keeping the variant that restores a feasible end time with the smallest loss; then pairs if one drop is not enough (cap `MaxVariants`). |
| `RouteDeviation` on target T | `Reorder`: best order of the Pending stops from the anchor (all permutations when 6 or fewer stops, otherwise nearest-neighbour), mandatory stops retained; else `ReduceStops`. If the order equals the current order the variant is discarded (nothing to propose). |

A variant is chosen as the first feasible one in the table order; among feasible variants of the same strategy, the smallest `arrivalTimeDiffMinutes` wins, ties by `travelTimeDiffMinutes`. A variant that is identical to the current plan is never stored.

## 5. REST API (Traveler role, session owner only)

All routes sit under `/api/v1/navigation-sessions/{sessionId}/rerouting-proposals/{proposalId}` (`NavigationSessionsController`). Authorization uses the authenticated user, never the route ids alone; a mismatched session/proposal or another user's session returns `404` for proposal lookups (same non-disclosure rule as UC-14 acknowledge), `403` is not used for foreign sessions. Follows `Result<T>` + `HandleFailure`, no response envelope.

| Method and path | Purpose | Success |
| --- | --- | --- |
| `GET .../{proposalId}` | Read proposal for the comparison screen | `200` detail |
| `PUT .../{proposalId}/acceptance` | Accept Re-routing | `200` `NavigationSessionResponse` of the session now bound to the new version |
| `PUT .../{proposalId}/rejection` | Keep Current Route | `204` |

Detail response: `proposalId`, `incidentId`, `status` (`Proposed|Accepted|Rejected|Expired`, effective status: a `Proposed` row past its TTL is returned as `Expired`), `reasonCode` + `reason` structured data (strategy, affected item/POI names), `currentStops[]`, `proposedStops[]` (with `change`), `affectedStops[]`, `travelTimeDiffMinutes`, `arrivalTimeDiffMinutes`, `proposedAtUtc`, `expiresAtUtc`, `decidedAtUtc?`. Mobile maps `Rejected` to its `declined` state. No coordinates of the traveler are accepted or returned.

Errors (HTTP / code / SRS message for Mobile):

| Case | HTTP | Code | Message mapping |
| --- | --- | --- | --- |
| Session or proposal not found / not yours | 404 | `rerouting.not_found` | MSG44 |
| Proposal expired (TTL passed, session ended, base stops changed) | 409 | `rerouting.proposal_expired` | MSG44 |
| Proposal already closed the other way (e.g. accept after reject) | 409 | `rerouting.proposal_closed` | MSG44 |
| Fresh evaluation at accept time no longer feasible | 409 | `rerouting.proposal_infeasible` | MSG44 |
| Persistence/transaction failure, nothing changed | 500 | existing server-error mapping | MSG127 |

Replays (accept on `Accepted`, reject on `Rejected`) succeed and change nothing.

## 6. Accept algorithm (single transaction)

`ExecuteInSerializableTransactionAsync` + `IItineraryMutationLock.AcquireAsync` (series resource resolved like `AdjustItineraryItemsCommandHandler`), retried up to 3 times on `DbUpdateConcurrencyException`/deadlock with tracked entities cleared.

1. Load proposal with incident, session (items, itinerary, scheduling request). `404` if session/proposal mismatch or `session.TravelerUserId != caller`.
2. Re-check current itinerary access like the other session mutations (`IItineraryAccessService.ResolveCurrentAsync` under serializable reads).
3. **Replay/closed:** `Accepted` -> return the current session response (no writes). `Rejected` -> `409 proposal_closed`. `Expired` -> `409 proposal_expired`.
4. Apply lazy session expiry (`ExpireIfDue`). If the session is terminal -> expire the proposal (`decided_at = now`), commit that write, return `409 proposal_expired`.
5. **Lazy proposal expiry (verified requirement 2.2):** if `now > proposed_at + ProposalTtl` -> `Expire(now)`, commit, return `409 proposal_expired`. Nothing else changes.
6. **Staleness fence:** every `baseStops.itineraryItemId` must still be `Pending` in the session. If the Traveler reached or skipped one meanwhile, or the itinerary series moved on, expire the proposal, commit, `409 proposal_expired`.
7. **Re-evaluate at accept time:** run the proposed stop order through `EvaluateStopVariantsAsync` with a fresh `EvaluationAnchor` (now, same rules as section 4.2). If infeasible -> expire the proposal, commit, `409 proposal_infeasible`. If feasible, the **freshly computed plan is the one applied** (times can differ slightly from what was displayed; it can never violate opening hours or the day end).
8. **New version (BR-26/27, PC-01):** build successor `Itinerary` in the same scheduling series, `Version = current.Version + 1`, items = copies of the current version's non-Pending items (Reached/Skipped, original times and sequence) followed by the new plan items (sequence continues). Provenance `reroute`. `Accept(now)` makes it Active. The old version rows are untouched. New method on `IItineraryVersionService` (does not reuse the whole-day `CreateAdjustedVersionAsync`).
9. **Session rebind** (new domain method `TripSession.RebindToRerouteVersion(newItinerary, newPendingItems, now)`): `ItineraryId` moves to the new version; `RequestedItineraryId`, state, history and Reached/Skipped `TripSessionItem` rows stay as they are. Pending snapshot rows are **replaced** (delete + insert, because `ItineraryItemId` is part of the key) by snapshots of the new version's remaining visit items with sequence numbers continuing after the kept rows. If the exploring/next target no longer exists, `ExploringItemId` is unchanged (it points at a Reached row). A `TripStateHistory` row records the change (`reason = rerouting_accepted`, `triggered_by = Traveler`) without changing the FSM state.
10. **Incidents:** every open incident bound to a replaced Pending row is resolved (`Resolve(now)`); the triggering incident included. Still-valid conditions are re-detected by the scanner under the new item ids (source keys embed the item id), so nothing is lost or duplicated. Incidents on Reached rows (overstay) are untouched. `affected_itinerary_item_id` of a resolved incident whose row was replaced is repointed to the new row of the same POI, or set to `NULL` when that POI left the plan (FK is `NoAction`, nullable).
11. **Siblings:** every other `Proposed` event of the session becomes `Expired` (`decided_at = now`) because its base stops are gone.
12. Mark the proposal `Accepted`, `decided_at = now`; `MarkTripSessionProgressForConcurrencyCheck(session)`; one `SaveChangesAsync`; commit.
13. Return the enriched `NavigationSessionResponse` (`ActiveRouteNavigationProjection.EnrichAsync`, new `itineraryId` and version) -> Mobile shows MSG42.

Failure handling: any exception before commit rolls everything back; the current version and session stay in force and the API returns `500` (MSG127). SRS abnormal case 8.a1 ("session cannot be updated, accepted version stored, restart navigation") is intentionally **not** implemented: with one transaction there is no half-applied state, which satisfies PC-01 and PC-04 more strongly. This is a deliberate deviation recorded for the developer (D5).

## 7. Reject (Keep Current Route) and expiry

- Reject: same steps 1-2 and 4, then: `Rejected` -> `204` replay; `Accepted`/`Expired` -> `409 proposal_closed` / `proposal_expired`; lazy expiry as step 5 (an expired proposal cannot be rejected, it is already closed); otherwise `Reject(now)`, commit, `204`. Itinerary and session are untouched (PC-02).
- Sweeper: the existing 10-minute scan marks overdue `Proposed` events `Expired` (also when the session ended). Housekeeping only (2.2).

## 8. Alert list integration

`ListTripAlertsQueryHandler` already returns `reroutingProposalId` for the newest `Proposed` event of an incident. Change: only return it when the proposal is not past its TTL, so Mobile never offers an expired proposal. No contract change otherwise.

## 9. Test plan (TDD, red-green)

Unit (Application/Domain): `ReroutingEvent` transitions and TTL boundary (`proposed_at + 15m` valid, `+15m +1s` expired); `ReroutingOptions` validation; each strategy table row (feasible, infeasible, mandatory kept, identical-to-current discarded); anchor builder (explore vs navigating, no reached stop, budget deduction); `EvaluateStopVariantsAsync` with and without `EvaluationAnchor` (existing tests unchanged), radius override keeps a POI outside the trip radius; `TripSession.RebindToRerouteVersion` (history kept, sequences, FK-safe order); Accept handler: success, replay, lazy expiry, stale base stop, infeasible at accept, terminal session, foreign user, other proposal expired.

Integration (SQL Server, existing `TripMateApiFactory` pattern): scanner creates one proposal for a `SevereWeather` incident and none for `WeatherAdvisory`/overstay; no second proposal while one is open; accept creates exactly one `Version+1` itinerary, keeps the old one, rewrites Pending rows, resolves incidents; **double tap / parallel accept** produces one version and two `200`s; reject then accept returns `409`; accept after TTL returns `409` without any change; failure injected during the write leaves version and session untouched; Admin Active Trip Details still renders.

Gates before the branch is finished: `dotnet test` green on the three suites, and no change under `Scheduling/Common|Csp|Routing` (so the solver report test is unaffected).

## 10. Delivery slices (each its own TDD task; plan file after approval)

- T1 this spec and `ReroutingOptions`.
- T2 Domain: `ReroutingEvent` factory and Accept/Reject/Expire, `TripSession.RebindToRerouteVersion`.
- T3 `EvaluationAnchor` on the evaluator + rerouted-version builder.
- T4 Engine: anchor builder, strategies, selection, snapshot v1 writer.
- T5 Triggers: scanner and `ReportDeviation` hooks, one-open-proposal rule, sweeper, alert-list TTL filter.
- T6 API: GET, accept, reject (controller, validators, error mapping, OpenAPI).
- T7 Accept pipeline (section 6) with concurrency and replay tests.
- T8 SQL integration suite, UAT seed for a `Proposed` proposal (valid snapshot v1), handoff document.

## 11. Decisions I made that need your confirmation

- **D1 Rebind by replacing Pending snapshot rows** (section 6 step 9-10) instead of a `superseded` column: no schema change and every existing reader of `session.Items` (navigation, scanner, trip history counts) stays correct. Cost: resolved incidents on replaced rows lose or repoint their affected item. The old/new plan history stays in the retained old itinerary version and in `proposed_itinerary_snapshot`.
- **D2 Accept re-evaluates and applies the fresh plan** instead of applying the stored times verbatim. Guarantees BR-25 at application time; the displayed times can drift by minutes.
- **D3 No GPS**: `RouteDeviation` proposals can only reorder or drop remaining stops from the last reached stop; they cannot reflect where the traveler actually is. A precise alternative needs a position source, which would be a separate task (UC-22/location logging).
- **D4 One open proposal per session; one proposal (not a menu) per incident**, and mandatory stops are never dropped or replaced (they may be postponed on closure).
- **D5 Single-transaction Accept** replaces SRS abnormal case 8.a1 (see section 6).
- **D6 Public contract names**: `Rejected` status literal, `PUT .../acceptance` and `.../rejection`, error codes in section 5.
