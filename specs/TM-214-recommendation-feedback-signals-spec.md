# TM-214 — Capture Recommendation Feedback & Behavioral Signals — Spec

Status: Approved — implemented and verified (TM-214 Step 4)
Binding design baseline: TM-214 Step 2A — FINAL FROZEN DESIGN (Step 2 + Step 2A corrections)
Evidence base: TM-214 Step 1 / Step 1B audits (HEAD `4254be8` → tree-identical at `e8013f98`)
Branch: `feature/phuctv-tm214-recommendation-feedback-signals`
Head at spec creation: `e8013f98a49b9fa1fbe4fcc412b562f7db8c37eb`

---

## 1. Status / Baseline

Design-frozen (Step 2/2A). This spec turns the frozen design into the
implementation contract. Implementation starts only from the verified TM-214
baseline state: expected HEAD, no tracked `src/`/`tests/` diff, only the two
expected untracked Step-3 artifacts (this spec and its plan) present.

## 2. Purpose

Capture traveler recommendation feedback (Like, Dislike, Skip, Reorder) as
append-only behavioral events, without touching scheduling, ranking, or any
existing HTTP contract. TM-215 consumes the captured data later; TM-214 ships
capture only.

## 3. Scope

- One new append-only table `social.RecommendationBehaviorEvents` (later, via
  a NEW idempotent migration under `database/migrations/`).
- Domain enums `RecommendationEventType` {Like, Dislike, Skip, Reorder} and
  `RecommendationCaptureSource` {Itinerary, Explore, PoiDetail}.
- Domain entity + EF mapping + one capture command (validator/handler/DTOs)
  + `POST /api/v1/recommendation-feedback`.
- Idempotent capture keyed by `clientEventId`.
- Tests: domain factory, EF schema parity, application unit, SQL integration
  (incl. concurrency), OpenAPI, scheduling regression gate.

## 4. Non-Goals

AI model · LLM · embeddings · collaborative filtering · recommendation
weighting · score calculation from behavior · automatic TravelerProfile
updates · PreferenceScore changes · direct itinerary re-ranking · CSP changes
· itinerary reorder UI/mutation · weather/risk logic · multi-day planning ·
background model training · Review write API (deferred, §18) · FavoritePOIs
mapping (deferred, §19).

## 5. Existing-State Summary (audited)

- `catalog.FavoritePOIs` exists schema-only (`tripmate_schema_v7.sql:406–411`,
  bookmark semantics); untouched by TM-214.
- `social.Reviews` (v7:1363–1380, `ReviewConfiguration.cs`) owns rating data;
  no write API exists; read-side aggregation in POI detail/explore.
- No Dislike/NotSuitable, no persisted Skip, no itinerary reorder (only
  TourMedia reorder), no behavior/event store for travelers.
- Scheduling consumes `TravelerProfiles.InterestTagsJson` via
  `TravelerPreferenceScoring.cs` only; feedback data will be read by nothing
  in scheduling.

## 6. Domain Semantics

- `FavoritePOIs ≠ Like`. FavoritePOIs remains bookmark state and is untouched.
- Rating remains exclusively in `social.Reviews` — never in events.
- Skip ≠ Dislike: Skip is positional/weak ("not in this plan"); Dislike is
  explicit negative valence toward the POI.
- Capture/persistence only: events never alter itineraries, never influence
  feasibility, and are read by nothing in scheduling.
- TM-215 is the future consumer; TM-216/CSP still owns feasibility.

## 7. Event-Type Definitions

| Type | Meaning | Context |
|---|---|---|
| `Like` | positive valence toward a POI | optional (source-coupled, §8) |
| `Dislike` | explicit negative valence toward a POI | optional (source-coupled, §8) |
| `Skip` | traveler passes over a recommended POI at its position | required (itinerary) |
| `Reorder` | traveler repositions a recommended POI — positions are **per action** | required (itinerary) |

Exactly four types; extension requires a migration (deliberate friction).

## 8. Source Definitions

| Source | Meaning | Context rule |
|---|---|---|
| `Itinerary` | signal captured from the traveler's generated plan | `itinerary_id` **required** |
| `Explore` | signal captured from POI exploration listing | `itinerary_id` **must be null** |
| `PoiDetail` | signal captured from POI detail view | `itinerary_id` **must be null** |

Frozen invariant: **itinerary context is present if and only if
`source = Itinerary`.** Skip/Reorder require `source = Itinerary`.

## 9. Final Data Model

One new append-only table, `social.RecommendationBehaviorEvents` (social
schema — the traveler-generated-signals area, `tripmate_schema_v7.sql:1271`).
Conceptual DDL — to be materialized **only** as a NEW idempotent migration in
the implementation phase (§21); never created in Step 3:

```sql
CREATE TABLE social.RecommendationBehaviorEvents (
    event_id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    traveler_user_id  BIGINT NOT NULL,
    poi_id            BIGINT NOT NULL,
    itinerary_id      BIGINT NULL,
    event_type        VARCHAR(20) NOT NULL,
    original_position INT NULL,
    new_position      INT NULL,
    was_mandatory     BIT NULL,
    source            VARCHAR(20) NOT NULL,
    occurred_at_utc   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    client_event_id   UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT UQ_RecommendationBehaviorEvents_Traveler_Client
        UNIQUE (traveler_user_id, client_event_id),
    CONSTRAINT CK_RecommendationBehaviorEvents_SourceContext CHECK (
        (source = 'Itinerary' AND itinerary_id IS NOT NULL
            AND was_mandatory IS NOT NULL) OR
        (source IN ('Explore','PoiDetail') AND itinerary_id IS NULL
            AND was_mandatory IS NULL)),
    CONSTRAINT CK_RecommendationBehaviorEvents_TypeSource CHECK (
        (event_type IN ('Like','Dislike')) OR
        (event_type IN ('Skip','Reorder') AND source = 'Itinerary'
            AND itinerary_id IS NOT NULL)),
    CONSTRAINT CK_RecommendationBehaviorEvents_TypePositions CHECK (
        (event_type IN ('Like','Dislike')
            AND original_position IS NULL AND new_position IS NULL) OR
        (event_type = 'Skip'
            AND original_position IS NOT NULL AND new_position IS NULL) OR
        (event_type = 'Reorder'
            AND original_position IS NOT NULL AND new_position IS NOT NULL
            AND original_position <> new_position))
);
```

FKs (added by the migration, all **NO ACTION** to preserve history):
`traveler_user_id` → `dbo.Users` (Reviews precedent), `poi_id` →
`catalog.POIs`, `itinerary_id` → `planning.Itineraries`.

Rejected fields (frozen): `scheduling_request_id` (derivable via
`Itinerary.SchedulingRequestId`, `Itinerary.cs:107`), `itinerary_item_id`
(rejected because Skip resolves its item through the strict
`(itinerary_id, original_position)` match — unique per `UQ_ItineraryItems`,
`tripmate_schema_v7.sql:783` — while membership-based events are fail-closed
on ambiguity (422), so no stored item reference is required; note that
POI-per-itinerary uniqueness is **not** a database/domain invariant:
`UQ_ItineraryItems` covers `(itinerary_id, sequence_no)` only, and the
generator's visit/rest set-disjointness is an implementation property, not a
contract), `reason`, `metadata_json`, session/context id, `event_hash`.

## 10. Field-by-Field Semantics

| Field | Semantics |
|---|---|
| `event_id` | BIGINT identity PK; stable reference and TM-215 cursor. |
| `traveler_user_id` | Owner; **always from the authenticated principal**, never a request field. |
| `poi_id` | Signal target; every event type is POI-scoped. |
| `itinerary_id` | Plan context; present ⟺ `source = Itinerary`; ownership-validated. |
| `event_type` | Frozen four-value enum (§7). |
| `original_position` | Skip: skipped item's position at action time (strict-matched). Reorder: position **immediately before this specific action** (per-action; NOT as-generated; not position-matched). Null for Like/Dislike. |
| `new_position` | Reorder only: resulting position for **this** action; ≠ `original_position`; in `1..item count`. Null otherwise. |
| `was_mandatory` | **Server-derived**, never client-supplied: from the unique matched itinerary item (Skip: strict position+POI match; contextual Like/Dislike and Reorder: the single item holding the POI — **zero or multiple matches → 422, no arbitrary resolution**). NULL for contextless Like/Dislike. Snapshot is intentional: items may mutate in later UCs. |
| `source` | Capture surface (§8); validated enum. |
| `occurred_at_utc` | Event time; entity-set via `IDateTimeProvider` + DB default `SYSUTCDATETIME()`; `.AsUtcDateTime2()`. |
| `client_event_id` | Client-generated non-empty GUID (UUID) idempotency token (body field) — no version restriction. |

## 11. DB Constraints / FKs / Indexes

- PK `event_id`; UNIQUE `(traveler_user_id, client_event_id)`; CHECKs
  `CK_…_SourceContext` (source ⟺ itinerary context **and** `was_mandatory`
  coupling), `CK_…_TypeSource` (Skip/Reorder ⇒ Itinerary source + context),
  and `CK_…_TypePositions` (§9) — DB-level defense-in-depth; the schema itself
  rejects the adversarial row `Skip` + `Explore` + NULL `itinerary_id` +
  `original_position = 1`. Primary enforcement is the domain factory +
  validator.
- FKs: `traveler_user_id`→Users, `poi_id`→POIs, `itinerary_id`→Itineraries,
  all NO ACTION (history preservation; POIs soft-delete via Status).
- Indexes: `(traveler_user_id, occurred_at_utc)`, `(poi_id)`, `(itinerary_id)`.
- All `datetime2` via `.AsUtcDateTime2()`; enum columns `VARCHAR(20)` via
  `HasConversion<string>()` + `HasMaxLength(20)` + `IsUnicode(false)`.

## 12. Event Validation Matrix

| Type | `source` allowed | `itinerary_id` | `original_position` | `new_position` | `was_mandatory` | POI rule |
|---|---|---|---|---|---|---|
| Like | Itinerary / Explore / PoiDetail | required iff `source=Itinerary`, else null | null | null | derived if contextual, else null | contextual: **exactly one** itinerary item must hold the POI — 0 or >1 matches → 422 (Active not required); direct: exists + Active |
| Dislike | Itinerary / Explore / PoiDetail | same as Like | null | null | same as Like | same as Like |
| Skip | Itinerary only | **required** | required | must be null | derived from the uniquely matched item | exists + **strict match** (`SequenceNo == original_position && PointOfInterestId == poi_id`; no Kind condition — named rest stops are matchable; at most one item per `UQ_ItineraryItems`); Active not required |
| Reorder | Itinerary only | **required** | required | required | derived from the single item holding the POI — 0 or >1 matches → 422 | exists + **exactly one** matching itinerary item (position match **not** required — per-action semantics, no mutation tracking); positions in `1..item count`, `original ≠ new` |

Global invariants: itinerary context ⟺ `source = Itinerary` · Skip/Reorder ⇒
`source = Itinerary` · POI row missing → 404 in all cases · inactive POI → 404
only for direct feedback · membership failure → 422.

## 13. Authorization / Ownership Rules

1. Authenticated Traveler only: `[Authorize(Roles = nameof(UserRole.Traveler))]`
   (repo convention: `SchedulingRequestsController.cs:16`,
   `TravelGroupsController.cs:26`); authenticated non-Traveler → 403 via the
   existing middleware ProblemDetails handler.
2. `traveler_user_id` from `ICurrentUserService` exclusively; DTO carries no
   traveler field.
3. `itinerary_id` supplied → itinerary must exist **and**
   `Itinerary.TravelerUserId == caller`; not found **or not owned** → **404**
   `feedback.itinerary_not_found` (repo convention:
   `CreateTravelGroupCommandHandler.cs:17–23`).
4. Contextual events: the POI must resolve to **exactly one** itinerary item —
   zero or multiple matches → **422** `feedback.context_mismatch` (fail-closed;
   POI-per-itinerary uniqueness is not a DB/domain invariant —
   `UQ_ItineraryItems` covers `(itinerary_id, sequence_no)` only,
   `tripmate_schema_v7.sql:783`).
5. Direct events: POI must be Active → else **404** `feedback.poi_not_found`.
6. Mandatory POIs may receive any signal (`was_mandatory=true` recorded).
7. No admin surface; other travelers' itineraries are 404.

## 14. Idempotency / Concurrency Behavior

- `clientEventId`: client-generated **non-empty GUID (UUID) in the request
  body** — no version restriction (any non-empty `Guid` value is accepted);
  required, `Guid ≠ Empty`.
- Uniqueness: DB **UNIQUE `(traveler_user_id, client_event_id)`** arbitrates
  concurrency. No scheduling-style `RequestHash`, app lock, or serializable
  transaction is used.
- **Frozen handler ordering**: after authentication and shape validation
  (400-level, validator-owned), the idempotency lookup by
  `(traveler_user_id, client_event_id)` AsNoTracking is the **first handler
  step** — it precedes all mutable resource/context validation.
- Existing token → compare the persisted client-supplied payload fields
  (`eventType`, `poiId`, `itineraryId`, `originalPosition`, `newPosition`,
  `source`; server-derived `was_mandatory` excluded) **without revalidating
  any current resource state** — current POI Active state, itinerary
  membership, and current positions are irrelevant for an existing token:
  - identical payload → **200 OK**, existing event returned (e.g., a retry
    after the POI later became inactive still replays 200, never 404);
  - different payload → **409** `feedback.event_token_conflict` (even when
    the new payload's POI/context would otherwise be invalid — the conflict
    resolves before any 404/422 validation can run).
- No existing token → append path: validate POI (direct Active rule),
  itinerary ownership/context, membership/Skip/Reorder rules, derive
  `was_mandatory`, create entity, `SaveChangesAsync`.
- Race (two concurrent first submissions): the handler catches
  `DbUpdateException` — an Application-layer precedent
  (`AuditFailureBehaviour.cs:41`, `GoogleAuthCommandHandler.cs:188`); **no
  provider-specific `SqlException` inspection is used, because
  `TripMate.Application` has no `Microsoft.Data.SqlClient` reference**. The
  handler then re-queries the winning event **AsNoTracking** by
  `(traveler_user_id, client_event_id)` and applies the same payload
  comparison → 200 vs 409; if the winning row cannot be found, the exception
  is rethrown (unexpected failure — fail-closed). **`SaveChangesAsync` is
  never called again on any post-exception path, and no
  `Entry(...)/Detach` is performed**: `IApplicationDbContext` exposes neither
  `Entry` nor `ChangeTracker` (verified — its surface is DbSets,
  `SaveChangesAsync`, `ClearTrackedEntities()`, and transaction executors),
  the abandoned `Added` entity is harmless because the scoped context is
  disposed with the request and no further save occurs, and the existing
  `ClearTrackedEntities()` abstraction (`IApplicationDbContext.cs:82–87`,
  UC-04 BR-02 race precedent) is the documented fallback **only if** a future
  variant must retry saving within the same request. Exactly one row is ever
  persisted per token.
- No payload hash column: the payload is fully persisted.

## 15. API Contract

**`POST /api/v1/recommendation-feedback`** — the only new endpoint;
`[Authorize(Roles = nameof(UserRole.Traveler))]` (repo convention:
`SchedulingRequestsController.cs:16`); controller builds the command with the
principal-derived user (pattern: `OperatorTourMediaController.cs:93`).

Request:

```json
{
  "clientEventId": "0d9a6c1e-…",
  "eventType": "Skip",
  "poiId": 123,
  "itineraryId": 55,
  "originalPosition": 3,
  "newPosition": null,
  "source": "Itinerary"
}
```

No `travelerUserId`, no `wasMandatory`, no `schedulingRequestId`, no rating.

Response (201 new / 200 retry): `eventId`, `clientEventId`, `eventType`,
`poiId`, `itineraryId?`, `originalPosition?`, `newPosition?`,
`wasMandatory?`, `source`, `occurredAtUtc`.

## 16. HTTP / Error Contract

| Situation | Status | Code |
|---|---|---|
| New event | 201 Created | — |
| Identical retry | 200 OK | — |
| Unauthenticated | 401 | — |
| Authenticated non-Traveler role | 403 | — (middleware ProblemDetails convention, `ProblemDetailsAuthorizationMiddlewareResultHandler.cs`) |
| Malformed contract / enum / shape (per-type position nullability, source-context coupling, Skip/Reorder with non-Itinerary source, empty `clientEventId`) | 400 | FluentValidation messages |
| POI missing; direct feedback on inactive POI; itinerary missing/not owned | 404 | `feedback.poi_not_found` / `feedback.itinerary_not_found` |
| Same token + different payload | 409 | `feedback.event_token_conflict` |
| Context/member mismatch (Skip strict match fails; POI resolves to zero or multiple itinerary items; Reorder positions out of range; **Reorder `original == new`**) | 422 | `feedback.context_mismatch` |

The 404/422 resource/context validations apply **only to the append path**
(no existing token); an existing `clientEventId` resolves to 200/409 first
(§14) and never revalidates current POI/itinerary/position state. Error
constants in a new `FeedbackErrorCodes.cs` (pattern:
`SchedulingErrorCodes.cs`); status mapping follows the project's
`Result<T>`/`HandleFailure` convention (AGENTS.md §2.3). Existing scheduling
HTTP contracts remain byte-identical.

## 17. Append-Only Semantics

Events are never updated or deleted by TM-214 code; every capture appends a
new row. History (sequence of signals with per-action positions) is the
product TM-215 consumes. DB-level immutability hardening (triggers/permissions)
is explicitly deferred.

## 18. Reviews / Rating Handoff

`social.Reviews` remains untouched and needs **no schema change** for a future
write UC. The events table contains **no rating data** (no dual source of
truth). Deferred to the future Reviews UC (not TM-214): a review write
endpoint reusing `Review.Create` validation (`Review.cs`: Rating 1–5,
POI-only Scenic/Photo, Comment ≤1000), its booking/post-trip producer rules
(no producer exists today), and resolution of the missing per-traveler
uniqueness on `social.Reviews`.

## 19. FavoritePOIs Decision

`catalog.FavoritePOIs` is **completely untouched** by TM-214: no EF mapping,
no API, no DDL. `FavoritePOIs ≠ Like` (bookmark state vs behavioral event);
Like lives only as `RecommendationBehaviorEvents.event_type = 'Like'`. The
table remains schema-ready for a future bookmarks UC.

## 20. Scheduling / CSP Boundary

Frozen — TM-214 must not change: `TravelerPreferenceScoring.cs`,
`CreateSchedulingRequest*` (command/validator/handler),
`ItineraryGenerationService.cs`, `SchedulingRequestsController.cs`,
`PreferenceScore`, candidate ordering, hard constraints
(TM-216/CSP territory). The events table is read by nothing in scheduling.

## 21. DB Migration Requirements

The schema change is introduced **only** as a NEW idempotent SQL script,
`database/migrations/<YYYYMMDD>_add_recommendation_behavior_events.sql`
(actual date at implementation), following the guarded style of
`20260914_add_scheduling_request_generation.sql` / `20260915_extend_scheduling_request_contract.sql`
(`IF COL_LENGTH`/`IF NOT EXISTS` guards, contract-`THROW` validation).
Historical migrations are never modified; no DDL outside
`database/migrations/`; the migration is **not** created in Step 3.

## 22. Security / Privacy Considerations

Events are user-initiated explicit actions (no implicit tracking). Capture
contains no free text (no `reason`). Ownership/authorization per §13. A
privacy/consent policy for **TM-215 consumption** is a required product
decision before any model use of this data (flagged as handoff; capture
itself is not blocked). No PII beyond the principal-derived `traveler_user_id`.

## 23. Backward Compatibility

No existing table, endpoint, or behavior changes. The new endpoint is
additive; OpenAPI gains one operation; all existing suites (scheduling, POI,
auth, groups, tours, media) remain green unmodified. If the traveler has no
feedback history, nothing in any existing flow changes.

## 24. Acceptance Criteria

1. Like captured from Explore and from PoiDetail (201; Active POI; no
   itinerary context for these sources).
2. Like/Dislike captured with itinerary context (owned itinerary; POI resolves
   to **exactly one** item — 0 or >1 matches → 422; Active status not
   required; `was_mandatory` derived).
3. Dislike captured direct (201; Active POI; no itinerary context).
4. Skip captured with owned itinerary + strict position/POI item match;
   `was_mandatory` derived; `new_position` rejected.
5. Reorder captured with per-action positions (`original ≠ new`, in `1..count`);
   membership-validated without position-bound matching; no itinerary mutation.
6. Authenticated Traveler role only (`nameof(UserRole.Traveler)` — 403 for
   other authenticated roles); `traveler_user_id` from principal only;
   not-owned/missing itinerary → 404; DTO carries no traveler field.
7. POI/context validation: missing POI → 404 always; inactive POI → 404 for
   direct feedback, **accepted** for itinerary-context feedback (historical
   preservation); itinerary not owned / missing → 404; POI resolves to zero or
   multiple itinerary items → 422; Skip wrong `original_position` → 422;
   Reorder `original == new` → 422; Reorder out-of-range → 422.
8. Mandatory POIs accept Skip/Reorder with `was_mandatory=true`.
9. `clientEventId`: empty GUID → 400; duplicate same payload → 200 existing;
   duplicate different payload → 409; concurrent duplicates → exactly one row,
   resolved 200/409; race resolution re-queries the winner **without** a
   second `SaveChangesAsync` and without entity detach; **idempotency
   precedes resource validation** — an identical retry after the POI later
   became inactive → 200 existing (never 404), and a token reused with a
   different payload that would otherwise be invalid → 409 (never 404/422).
10. Append-only: no update/delete paths; prior events preserved.
11. Persistence mapping asserts schema parity (columns, types, nullability,
    VARCHAR(20) non-Unicode enums, indexes, FKs NO ACTION); DB CHECK
    constraints enforce the source/context, `was_mandatory`, and per-type
    position invariants — the adversarial row `Skip` + `Explore` + NULL
    `itinerary_id` + `original_position = 1` is rejected by the schema.
12. OpenAPI gains exactly the new operation; existing contract snapshots
    unchanged.
13. Zero scheduling/ranking behavior change: frozen files untouched; full
    existing suite green unmodified.
14. Migration (when implemented) is a new idempotent file under
    `database/migrations/`; historical migrations untouched; no DDL in Step 3.
15. No magic strings: enums/constants per AGENTS.md §4.1; `AsUtcDateTime2()`
    applied.

## 25. Explicit Implementation Handoff

To the implementation plan: entity/enums/configuration workstream, capture
feature (command/validator/handler/DTOs/errors), controller, migration file,
test set (§24), and the frozen-file boundary (§20). To TM-215: append-only
event stream with positional context, queryable by
`(traveler_user_id, occurred_at_utc)` and `(poi_id)`. To the future Reviews
UC: §18. To product: privacy/consent decision before TM-215 consumption (§22).
