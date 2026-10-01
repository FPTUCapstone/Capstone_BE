# TM-217 Backend Technical Specification — UC-10 LLM Explanation for CSP-Validated Itinerary

## 1. Document Control

| Field | Value |
| --- | --- |
| Task ID | TM-217 |
| Use case | UC-10 — Generate LLM Explanation for CSP-Validated Itinerary |
| Status | Spec Ready — awaiting developer approval |
| Date | 2026-10-01 |
| Baseline | `develop` @ `37dfabbe77714eba5f4b6a58d572d32a9c8d5692` (TM-216 merged) |
| Branch | `feature/phuctv-tm217-itinerary-explanation` |
| Predecessor context | TM-215 (AI POI ranking, deterministic fallback), TM-216 (AI/CSP responsibility boundary) |

## 2. Executive Summary

After the CSP scheduler produces and persists a validated itinerary, a single structured
LLM call generates a short Vietnamese "friendly explanation" per itinerary item. The
scheduler transaction always commits first; the explanation is best-effort afterward.
Explanations are persisted in a new nullable per-item column and returned on create and
replay/read paths. The LLM is a display-text generator only: it can never alter POIs,
order, times, durations, costs, mandatory flags, travel decisions, or feasibility.

## 3. Scope

### In Scope
- New Application-layer provider port + contracts for itinerary explanation.
- New Infrastructure HTTP provider (Gemini `v1/interactions`, same transport pattern as TM-215 ranking).
- One new nullable per-item column `friendly_explanation` + idempotent migration.
- `ItineraryItem.FriendlyExplanation` domain field + persistence configuration.
- `SchedulingItemDto.FriendlyExplanation` on the existing create/replay response.
- Deterministic Vietnamese fallback explanations.
- Post-commit two-phase flow in `CreateSchedulingRequestCommandHandler`.

### Out of Scope
- Itinerary-level AI summary (frozen decision 9).
- Any change to scheduling/feasibility/replay-identity behavior (frozen decision 20).
- Any change to `RecommendationReason` semantics or values (frozen decision 7).
- FE/Mobile contract changes beyond the additive DTO field.
- Background retry/backfill jobs, explanation editing/regeneration endpoints.
- Real Gemini calls in tests.

## 4. Source of Truth — Verified Baseline Architecture

- `POST /api/v1/scheduling-requests` → `CreateSchedulingRequestCommandHandler.Handle`
  (`src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`).
- CSP: `ItineraryGenerationService.GenerateAsync` → `Result<GeneratedItineraryPlan>`
  (immutable records; items carry hardcoded deterministic `RecommendationReason`).
- Persistence: `Itinerary.CreateCspGenerated` + `ItineraryItem.CreateVisit/CreateRest`
  inside `ExecuteInSerializableTransactionAsync`; `SchedulingRequest.Complete`.
- Response: `SchedulingResponseDto` / `SchedulingItemDto.RecommendationReason`;
  replay maps from DB via the second `ToResponse` overload.
- TM-215 pattern to mirror: `IPoiRankingProvider` port, `HttpPoiRankingProvider`
  (structured JSON `response_format`, `x-goog-api-key`, error mapping Network/Quota/
  ServerError/InvalidResponse), options + `IValidateOptions` validator, orchestrator
  timeout via linked `CancelAfter`, outcome logging, provider `Enabled` flag.

## 5. Frozen Decisions (binding)

1. The scheduler remains the final authority.
2. Explanation runs only for a successfully validated itinerary.
3. Provider is invoked only after the scheduling transaction has committed.
4. One structured LLM call per itinerary, not one call per item.
5. LLM output is text-only.
6. LLM cannot modify POI, item order, arrival/departure time, duration, cost,
   mandatory status, travel decisions, or feasibility.
7. Existing `RecommendationReason` is unchanged.
8. Add a separate persisted per-item `FriendlyExplanation` field.
9. No itinerary-level AI summary in TM-217.
10. For a normally completed Phase 2, persist either the validated LLM explanation
    or the deterministic fallback explanation. Accepted exceptions where
    `FriendlyExplanation` may remain NULL with no fallback persistence required:
    - caller cancellation after T1 has already committed;
    - process termination/crash between T1 and T2;
    - T2 explanation persistence failure.
    For all three exceptions: the scheduler result remains committed, no scheduling
    rollback occurs, `FriendlyExplanation` may remain NULL, and no fallback
    persistence is required after the request/process can no longer safely complete
    Phase 2.
11. Provider failure/timeout/malformed output must never fail an otherwise successful
    scheduling request.
12. Timeout: 5 seconds.
13. Normalized preference tokens only; no traveler identity in any payload.
14. LLM response must contain exactly the expected itinerary items, identified by
    sequence/item identity, with bounded text length.
15. Unknown/missing/duplicate items or invalid text ⇒ whole-response fallback.
16. TM-215 ranking reasons are not reused as authoritative explanations (they are
    already discarded at `PoiRankingSnapshotEntry`; this spec does not resurrect them).
17. Vietnamese explanation text.
18. Replays/read paths return the persisted explanation.
19. A DB migration is allowed only for the new nullable per-item explanation column.
20. No changes to scheduling feasibility behavior.

## 6. Architecture Boundary (Clean Architecture)

```
TripMate.Application/Features/Scheduling/Explanation/
    IItineraryExplanationProvider.cs      (port; Result<T> pattern)
    ItineraryExplanationInput.cs          (input records, immutable)
    ItineraryExplanationResult.cs         (output records, immutable)
    ItineraryExplanationValidator.cs      (all-or-nothing response validation)
    ItineraryExplanationFallback.cs       (deterministic Vietnamese strings)
TripMate.Infrastructure/AiExplanation/
    HttpItineraryExplanationProvider.cs   (Gemini interactions transport)
    ExplanationProviderOptions.cs         ("AiExplanation" section)
    ExplanationProviderOptionsValidator.cs (IValidateOptions)
TripMate.Domain/Entities/ItineraryItem.cs (+ FriendlyExplanation, +500 cap)
TripMate.Infrastructure/Persistence/Configurations/ItineraryItemConfiguration.cs
database/migrations/20261001_add_itinerary_friendly_explanation.sql
src/TripMate.Api/appsettings.json ("AiExplanation": Enabled=false default)
```

Dependency flow stays inward: Application defines the port; Infrastructure implements it
via `HttpClient` (no SDK); only the handler orchestrates; no layer above Application may
touch the provider. No synthetic response envelope; `Result<T>` + `HandleFailure` as-is.

## 7. Two-Phase Flow (binding behavior)

**Phase 1 — scheduling (unchanged).** The existing serializable transaction runs exactly
as today: lock, replay check, `SchedulingRequest.Create`, guard validations, CSP,
`Itinerary.CreateCspGenerated`, items, `Complete`, `SaveChangesAsync`, commit. If Phase 1
fails, the request fails exactly as today — no explanation code executes.

**Phase 2 — explanation (new, post-commit, best-effort).**
1. The handler captures the validated plan and persisted itinerary from Phase 1 and
   builds `ItineraryExplanationInput` (in-memory; no extra DB read).
2. Provider invoked once (5 s timeout). Validated output → LLM explanations; any
   failure/invalid output → deterministic fallback explanations.
3. Persist in a **short second transaction (T2)**: load the itinerary's
   `ItineraryItem`s with change tracking enabled (AGENTS.md §3.2), apply
   `AttachFriendlyExplanation` per item, `SaveChangesAsync`.
4. Response (201) always carries the computed explanations, whether T2 succeeded or not.

**Explanation persistence failure (the critical edge):**
- T2 failure is logged (outcome `persistence-failed`, warning) and swallowed.
- The already-committed scheduler result is never weakened: `SchedulingRequest` stays
  `Completed`, the itinerary/items keep their committed values, the response stays 201
  with the computed explanations.
- DB rows for `friendly_explanation` remain NULL; replay/read paths then return NULL
  for those items (decision 18 applies to what *is* persisted).
- No in-request retry (single T2 attempt). Background backfill is a non-goal.
- If the process dies between T1 and T2, the itinerary is committed without
  explanations — same observable result as T2 failure. This is accepted.

**Binding consistency clarification:** A Phase-2 explanation persistence failure may
cause the original create response to contain a `FriendlyExplanation` that is absent
from a later replay or read. This degraded consistency is explicitly accepted because
`FriendlyExplanation` is non-authoritative, best-effort presentation metadata. It must
never weaken, roll back, or alter the already-committed scheduling result.

**Replay and infeasible paths never call the provider** (decision 3: fresh validated
generation only). Replay returns persisted values as-is.

## 8. Provider Input Contract

```csharp
sealed record ItineraryExplanationInput(
    IReadOnlyCollection<ItineraryExplanationItem> Items,
    ItineraryExplanationContext Context);

sealed record ItineraryExplanationItem(
    int SequenceNo,
    long? PoiId,
    string? PoiName,
    string? CategoryName,
    IReadOnlyCollection<string> TagNames,
    ItineraryItemKind Kind,
    bool IsMandatory,
    DateTimeOffset PlannedArrivalUtc,
    DateTimeOffset PlannedDepartureUtc,
    int StayDurationMinutes,
    decimal? EstimatedCost,
    int? TravelDurationToNextMinutes,
    string CspRecommendationReason);

sealed record ItineraryExplanationContext(
    IReadOnlyCollection<string> PreferenceTokens,  // normalized, from
                                                   // TravelerPreferenceScoring.ParsePreferenceTokens
    DateTimeOffset StartAtUtc,
    string TimeZoneId,
    int TotalDurationMinutes);
```

**CategoryName / TagNames clarification:**
- `CategoryName` and `TagNames` are descriptive POI metadata only.
- They exist so the LLM can ground "Why this stop?" against `PreferenceTokens`.
- `TagNames` must be normalized/deduplicated before serialization.
- They do NOT grant the LLM any scheduling authority.
- Do NOT add ranking scores (`BaseScore`, `AiScore`, `EffectiveDesirabilityScore`,
  `ScenicQuality`, `PhotoQuality`), behavior aggregates, coordinates, or traveler
  identity to the input contract.

**Privacy payload restrictions (decision 13):** the serialized payload contains only the
fields above. Explicitly allowed descriptive content: `PoiName`, `CategoryName`, and
normalized/deduplicated `TagNames` — POI content, not identity, required to ground the
explanation against preference tokens. Explicitly excluded: traveler user id / email /
any account identifier, idempotency key, request hash, coordinates (latitude/longitude),
behavior-history aggregates, ranking scores internal to TM-215 (`BaseScore`,
`AiScore`, `EffectiveDesirabilityScore`, scenic/photo ranking inputs), auth material.

## 9. Provider Output Contract

Transport mirrors `HttpPoiRankingProvider`: Gemini `v1/interactions`, model/endpoint from
options, `x-goog-api-key` header, `Store: false`, strict JSON `response_format` schema:

```json
{
  "items": [
    { "sequenceNo": 1, "poiId": 28, "friendlyExplanation": "string" }
  ]
}
```

System instruction (essence): you are an itinerary explainer; write ONE short
Vietnamese sentence (≤ 500 characters) per supplied item, in item order; explain why the
stop fits the traveler's preferences and schedule; do not add, remove, reorder, or
reschedule stops; do not invent facts or identity; respond in Vietnamese only.

Application-level output record (after transport parsing):

```csharp
sealed record ItineraryExplanationResult(
    IReadOnlyCollection<ItineraryExplanationItemResult> Items);
sealed record ItineraryExplanationItemResult(
    int SequenceNo, long? PoiId, string FriendlyExplanation);
```

## 10. Validation Rules (all-or-nothing, decision 14/15)

The validator runs before any persistence. The LLM response is valid **only if all** hold:
1. `items` is present and its count equals the input item count.
2. Every input `SequenceNo` appears exactly once; no unknown sequence numbers; no duplicates.
3. Each item's `poiId` equals the input item's `poiId` exactly (null only where the
   input item has no POI, i.e. free-rest items).
4. `friendlyExplanation` is non-null, non-empty after trim, and ≤ 500 characters.
5. Transport-level checks: single model_output text, valid JSON against the schema.

Any violation ⇒ discard the entire response and use deterministic fallback for **all**
items (decision 15). No partial acceptance, no per-item repair, no truncation of
oversized text (stricter than TM-215 ranking, which truncated).

## 11. Deterministic Fallback Rules (decision 10, 17)

Fallback is computed per item from already-validated data, as exact constants:

| Item condition | Fallback text (Vietnamese) |
| --- | --- |
| Visit, IsMandatory = true | `Điểm đến bắt buộc theo yêu cầu của bạn.` |
| Visit, IsMandatory = false | `Điểm gợi ý phù hợp với sở thích và khung giờ của bạn.` |
| Rest with PoiId | `Điểm nghỉ ngơi được đề xuất trên lộ trình.` |
| Rest without PoiId | `Thời gian nghỉ ngơi tự do giữa các điểm đến.` |

Fallback text is defined as `public const` strings on `ItineraryExplanationFallback`
(Zero Magic Strings rule, AGENTS.md §4.1) and is used verbatim — deterministic, bounded
(≤ 500), locale-stable, and unit-testable without any provider.

## 12. Persistence Semantics

- New column: `planning.ItineraryItems.friendly_explanation NVARCHAR(500) NULL`
  (Unicode, matches Vietnamese; same cap as `recommendation_reason`).
- Domain: `ItineraryItem.FriendlyExplanation { get; private set; }` +
  `FriendlyExplanationMaxLength = 500` + `internal void AttachFriendlyExplanation(string? text)`
  applying the same normalization as `RecommendationReason` (trim, null-if-empty,
  throw on overflow). No changes to `RecommendationReason` or any factory signature.
- EF: `ItineraryItemConfiguration` maps the column; nothing else changes.
- Written only by Phase 2 (T2), tracked entities, one `SaveChangesAsync`.
- Historical rows and persistence-failure rows stay NULL — no guessed backfill
  (same policy as the audit migration).

## 13. Replay / Read Semantics

- `ReplayAsync` maps `FriendlyExplanation` from DB into `SchedulingItemDto` (decision 18).
- The create response carries the computed explanations from Phase 2 step 4.
- Persisted-NULL rows (historical or persistence-failure) surface as `null` — clients
  must treat the field as optional from day one.

## 14. DTO / Endpoint Changes

- `SchedulingItemDto` gains one trailing parameter `string? FriendlyExplanation`.
- No new endpoints, no route/status-code/ProblemDetails changes; the field is additive
  on the existing `POST /api/v1/scheduling-requests` 201 response and its replay.

## 15. Timeout / Error Behavior

| Condition | Outcome | Outcome label |
| --- | --- | --- |
| Provider disabled (`AiExplanation:Enabled=false`) | Fallback, no HTTP call | `provider-skipped` |
| 5 s timeout (linked CTS `CancelAfter`, mirroring TM-215) | Fallback | `timeout` |
| HTTP/IO exception | Fallback | `network` |
| HTTP 429 | Fallback | `quota` |
| HTTP 5xx | Fallback | `server-error` |
| 4xx / malformed JSON / schema/count/sequence/length violations | Fallback | `invalid-response` |
| Validated LLM output | LLM text persisted | `success` |
| T2 persistence failure | Response keeps computed text; rows stay NULL | `persistence-failed` |

Caller cancellation (`OperationCanceledException` with the caller token) must still
propagate — the request is being aborted anyway. If cancellation occurs after the
Phase 1 transaction has committed, it does NOT undo the completed `SchedulingRequest`
or the persisted itinerary; Phase 2 explanation persistence may remain incomplete and
`friendly_explanation` may stay NULL. This is an explicitly accepted best-effort
explanation outcome (see the accepted exceptions under frozen decision 10). Provider
failure never throws out of Phase 2 and never changes the Phase 1 result (decision 11).

## 16. Logging & Observability

Mirror `PoiRankingOrchestrator.LogOutcome` with a dedicated logger category:
`provider={ProviderName}; enabled={Enabled}; itemCount={N}; latencyMs={Ms};
outcome={Outcome}; fallbackUsed={Bool}; timedOut={Bool}` at Information (success,
provider-skipped) or Warning (all failure outcomes, persistence-failed). No payload,
API key, or explanation text is logged.

## 17. Security & Privacy

- Payload restricted per §8; no traveler identity leaves the process boundary.
- API key only via `x-goog-api-key` from options; never logged or serialized.
- LLM output is untrusted display text: stored bounded (≤ 500), returned as a plain
  string; clients render it as text. No server-side execution of LLM content.
- Secrets from configuration/UserSecrets only (AGENTS.md §5.4); `AiExplanation.Enabled`
  ships `false` in committed appsettings.

## 18. Acceptance Criteria

1. With a valid LLM response: every persisted item has `FriendlyExplanation` = the
   validated LLM text; `RecommendationReason` values are byte-identical to pre-TM-217.
2. With any invalid/malformed/timed-out/failed provider call: every item has the exact
   deterministic fallback string for its condition; the response is still 201.
3. The provider is called exactly once per fresh successful generation, never on replay,
   never on infeasible failure, never before the scheduler transaction commits.
4. POIs, order, times, durations, costs, mandatory flags, travel minutes, and feasibility
   outcomes are bit-identical with and without the provider (regression-proven by tests).
5. Replay returns persisted explanations; rows persisted as NULL replay as NULL.
6. `friendly_explanation` is NULL-able and the migration is idempotent (re-runnable).
7. All payload-privacy exclusions in §8 hold (unit-enforced on the serialized input).
8. Solution builds 0 warnings; existing suites stay at their baselines
   (Application 925+, Infrastructure 173/0/1, Generator 28, SQL API per current baseline).

## 19. Required Tests

Unit (Application):
- Handler: success path persists + returns LLM text; each invalid-response variant
  (wrong count, unknown/missing/duplicate sequence, poiId mismatch, empty text, >500)
  ⇒ whole-response fallback; timeout/network/quota/server-error ⇒ fallback; provider
  disabled ⇒ fallback with zero provider calls; persistence failure ⇒ 201 + computed
  text + `SchedulingRequest` still `Completed` + item values unchanged; replay returns
  persisted text and does not invoke the provider; fallback string table is exact.
- Validator: full all-or-nothing matrix of §10 (mirror `PoiRankingOrchestratorTests`).
- Input serializer: privacy exclusions of §8 are asserted on the serialized payload.

Infrastructure (AiExplanation):
- `HttpItineraryExplanationProvider`: 429→Quota, 5xx→ServerError, transport exception→
  Network, malformed JSON/missing output→InvalidResponse, valid body→parsed result —
  mirroring the existing `HttpPoiRankingProvider` test approach, fakes only.

Integration (SQL Server, `TripMateApiFactory` + fake provider):
- Fresh generation persists LLM text to `friendly_explanation`; replay returns it;
  provider-failure run persists fallback; `RecommendationReason` untouched throughout.
- Migration applied on fresh and existing databases (idempotent double-run).

No automated test may call Gemini or any external endpoint.
Production runtime code may call the configured itinerary explanation provider.
All automated tests must use fakes, stubs, or controlled test HTTP handlers.

## 20. Explicit Non-Goals

- Itinerary-level AI summary; explanation edit/regenerate/delete endpoints.
- Backfill of historical rows; background retry/backfill worker for persistence failures.
- Reuse of TM-215 ranking reasons; any new ranking-orchestrator coupling.
- Multi-language output beyond Vietnamese; streaming; prompt customization per tenant.
- Changes to request-hash/replay identity, CSP logic, `SchedulingGenerationOptions`
  behavior, or any scheduling endpoint contract beyond the additive DTO field.
- FE/Mobile implementation.

## 21. Open Issues / Unresolved Questions

1. Should shared-itinerary / other read surfaces expose `FriendlyExplanation` later?
   (Out of scope for TM-217; DTO is additive so it can be added without breaking.)
2. Does the synchronous POST accepting up to ~5 s added latency in Phase 2 need a
   product sign-off? (Worst case now ≈ ranking 5 s + explanation 5 s.)

## 22. Verdict

SPEC READY — pending developer approval before any implementation task is planned.
