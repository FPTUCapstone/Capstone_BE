# TM-213 — Traveler Preference Profile & Constraint Inputs — Spec

Status: Approved — implemented and verified (TM-213 Step 4)
Binding design baseline: TM-213 STEP 2 — FINAL CORRECTED CONTRACT & DESIGN REPORT
Branch: `feature/phuctv-tm213-traveler-preference-profile`
Head at spec creation: `4afd56cabcda7dbbd83062086121d32e77848885`

---

## 1. Purpose

Define the canonical TravelerPreferenceProfile contract; define, harden, and
fully specify the existing InterestTags preference read path — without changing
the scheduling HTTP contract, the database schema, or the request identity
semantics.

TM-213 is define-and-consume only: the current application code has no
discovered production write path for TravelerProfile rows, and no write path is
created here.

## 2. Scope

- Formalize the TravelerPreferenceProfile persisted contract (all six
  preference fields that exist in `dbo.TravelerProfiles`).
- Map all six fields in the EF Core read model for **persistence/schema parity
  only**.
- Harden and pin the **InterestTags** read/degradation/ranking path — the only
  field TM-213 consumes for scheduling.
- Pin backward-compatibility, read-path degradation, idempotency/live-read/
  replay semantics with tests.
- Document precedence, null/unspecified semantics, RiskTolerance semantics,
  and the handoff package for the future Traveler Preference UC.
- Test/dev mock-seed strategy for TravelerProfile data.

## 3. Non-Goals

- No TravelerProfile write API, service, or registration-time seeding.
- No SchedulingRequest HTTP/OAS contract change (no new fields, no optionalized
  fields, no schema filter changes).
- No database schema change and no EF migration (AGENTS.md §3.1: DB-first; the
  schema already exists in `database/tripmate_schema_v7.sql:217–232`).
- No scheduling behavior for TravelPace, RiskTolerance, or FoodPreferences.
- No server-side fallback for PreferredTransportMode or DefaultBudget.
- No system default substitution for null TravelPace or null RiskTolerance.
- No change to `request_hash` inputs or replay semantics.
- No AI/LLM work (TM-215+), no CSP work (TM-216), no multi-day work (TM-220),
  no UX work (TM-219), no behavioral-feedback work (TM-214).
- No weather integration, no risk scoring, no numeric thresholds.

## 4. Persisted Fields (Tier A — schema facts)

Source of truth: `database/tripmate_schema_v7.sql:217–232`
(`CREATE TABLE dbo.TravelerProfiles`).

| Field | DB column | SQL type / constraints |
|---|---|---|
| UserId (metadata) | `user_id` | BIGINT PK, FK → Users(user_id) ON DELETE CASCADE |
| InterestTags | `interest_tags_json` | NVARCHAR(1000) NULL — JSON array of tokens |
| PreferredTransportMode | `preferred_transport_mode` | VARCHAR(20) NULL, CHECK {Walking, Motorbike, Car, PublicTransit} |
| TravelPace | `travel_pace` | VARCHAR(20) NULL, CHECK {Relaxed, Moderate, Fast} |
| RiskTolerance | `risk_tolerance` | VARCHAR(20) NULL, CHECK {Low, Medium, High} |
| FoodPreferences | `food_preferences_json` | NVARCHAR(1000) NULL — JSON array |
| DefaultBudget | `default_budget` | DECIMAL(12,2) NULL |
| UpdatedAtUtc (metadata) | `updated_at` | DATETIME2 NOT NULL, DEFAULT SYSUTCDATETIME() |

**Persisted does not mean active.** Tier membership is defined in §5.

## 5. Field Tiers

### Tier B — Active TM-213 field (consumed by scheduling now)

| Field | Behavior |
|---|---|
| InterestTags | Soft preference only. Ranking boost in candidate selection (§6). Never a hard constraint; never filtered on; never persisted to the response; cannot override any feasibility gate. |

### Tier C — Profile defaults (contract-defined; consumed by the future
Traveler Preference UC / Mobile form prefill, not by the server)

| Field | Behavior |
|---|---|
| PreferredTransportMode | Prefills the required `transportMode` request field. Explicit request always wins. No server-side fallback in TM-213. |
| DefaultBudget | Prefills the optional `budgetVnd` request field (Advanced options). Explicit request always wins. No server-side fallback in TM-213. |

### Tier D — Deferred fields (contract-defined; no consumer in TM-213)

| Field | Status |
|---|---|
| TravelPace | Consumption deferred. Qualitative semantics documented (TM-56-traveler-research.md §2). Quantified mapping is the first requirement decision of the follow-up scheduling task. |
| RiskTolerance | Consumption deferred. Semantics documented in §9. |
| FoodPreferences | Consumption deferred. No documented consumer semantics (budget excludes food/drink). |

**EF mapping rule:** all six fields are mapped for **persistence/schema parity
only**. Mapping a field does NOT imply TM-213 implements behavior for it.
Tier C/D fields carry an XML doc marking them parity-only. The three
enum-valued fields (PreferredTransportMode, TravelPace, RiskTolerance) map as
non-Unicode VARCHAR(20) — `HasConversion<string>()` + `HasMaxLength(20)` +
`IsUnicode(false)`, or an explicit `varchar(20)` column type — with no behavior.

## 6. InterestTags Ranking Semantics and PreferenceScore

- The profile's `interest_tags_json` is parsed into normalized preference
  tokens: trim, Unicode FormD normalization with NonSpacingMark removal
  (diacritic folding), invariant lowercase, ordinal set.
- **PreferenceScore (derived signal)** — computed at generation time per POI:

  `score = (normalized poi.Category.Name ∈ tokens ? 100 : 0)
         + 100 × count(PoiTags whose normalized Tag.Name ∈ tokens)`

- Range: `0 … 100 × (1 + PoiTags.Count)`. Default when no tokens: `0`.
- Higher is better. Ties are resolved by the fixed total ordering below, so
  selection is fully deterministic.
- **Selection ordering** (optional candidates):
  `PreferenceScore desc → ScenicScore desc (null → MinValue)
   → PhotoRating desc (null → MinValue) → equirectangular distance asc
   → EstimatedVisitCost asc (null → MaxValue) → Id asc`.
- Mandatory POIs bypass preference ordering entirely (ordered by Id; validated
  for feasibility independently). PreferenceScore never affects feasibility,
  filtering, budget, or opening-hours gates — ranking only.
- PreferenceScore is **not persisted** on SchedulingRequest/Itinerary and does
  **not appear in the API response** (response reason strings are unchanged
  free-text values).

## 7. Precedence

For every preference: **explicit request value > profile value > evidenced
system default.** A system default exists only where repository evidence
defines one.

| Field | Explicit request | Profile | System default (evidence) | Applied where |
|---|---|---|---|---|
| TransportMode | `transportMode` (required) | PreferredTransportMode | Walking — `DF_SchedulingRequests_TransportMode` | Mobile prefill (future UC) |
| BudgetVnd | `budgetVnd` (optional) | DefaultBudget | none — absent means no budget constraint | Mobile prefill (future UC) |
| RestPreference | `restPreference` (required) | — | Auto — `DF_SchedulingRequests_RestPreference`; TM-56-spec.md:67–68 | Mobile, current |
| TravelPace | — | optional {Relaxed, Moderate, Fast} | **none / unspecified** | Future scheduling task |
| RiskTolerance | — | optional {Low, Medium, High} | **none / unspecified** | Deferred |
| InterestTags | deliberately none — no per-request override channel | InterestTags | none — absent profile means no boost, not a substituted value | Server-side, TM-213 |

## 8. Null / Unspecified Semantics

- A null profile field means **the preference is unknown**, never a specific
  value. The system must not silently convert unspecified into a default.
- `TravelPace = Moderate` means a stored Moderate preference;
  `TravelPace = null` means no pace preference is known.
- `RiskTolerance = Medium` means a stored Medium preference;
  `RiskTolerance = null` means the system does not know the traveler's
  preference.
- Any fallback for unspecified pace/risk is an explicit requirement decision of
  the future consuming task — never an implicit reader-side substitution.
  (Evidence note: research "Moderate: default balance" describes the balanced
  middle option of the scale, not a default for null; the DDL defines a DEFAULT
  constraint only on `updated_at`.)

## 9. RiskTolerance Semantics

**RiskTolerance = the traveler's tolerance / willingness to accept risk.**

- **Low** — prefer safer options.
- **Medium** — balanced.
- **High** — willing to accept more risky/adventurous options.

**RiskTolerance is NOT the actual risk level.** Actual environmental/safety
risk — severe storm, typhoon, dangerous weather warning, unsafe POI condition —
is a **separate, future actual-risk/safety input** modeled independently of
this profile field.

**Future rule:** RiskTolerance may influence soft ranking in low/medium-risk
situations and must **never override hard safety constraints**. Any actual-risk
input, when it exists, ranks above traveler willingness.

**TM-213 deliverable:** this definition enters the contract and the handoff
package (§14). No code consumes it; no weather integration; no risk scoring;
no numeric thresholds.

## 10. Backward Compatibility

The read path behaves identically whether profile rows are absent, present but
null, or populated by any writer:

| Profile state | Behavior |
|---|---|
| Row absent | No preference tokens → no ranking boost → existing fallback ordering (Scenic → Photo → distance → cost → Id) unchanged |
| NULL / empty / whitespace `interest_tags_json` | Safely degrade to no preference tokens |
| Malformed JSON | Safely degrade to no preference tokens; never a 4xx/5xx |
| Tokens matching nothing | Zero boost, no error (tolerant matching by design) |

"Row absent" is a code-level input state, not a claim about production data
contents. The repository evidence proves only that the current application
code has no discovered production path to create/update TravelerProfile rows;
it does not establish what the production database normally contains.

## 11. Read-Path Degradation and Length Policy

- The read path makes **no length-based decision**. Input longer than the
  1000-character column limit is a **future write-validation concern** owned by
  the Traveler Preference UC; it must not be silently treated as "no
  preference tokens". The read path parses whatever the column contains:
  oversized valid JSON parses normally; oversized invalid JSON falls into the
  malformed branch.
- All six preference fields are nullable. The five newly mapped preference
  fields remain nullable consistent with the DB schema; InterestTags was
  already nullable. DB CHECK constraints govern enum values regardless of
  writer.

## 12. Idempotency / Live-Read / Replay

- `request_hash` remains exactly the 14 request-contract fields. Preference
  values are **not part of request identity**.
- Preferences are **live-read** at generation time (`AsNoTracking`), never
  snapshotted onto SchedulingRequest or the response.
- Idempotent replay returns the **originally generated itinerary even if the
  profile changed since** the first generation. A traveler who wants new
  preferences submits a new IdempotencyKey.
- Generation determinism = f(request payload, profile at generation time, POI
  data, route matrix). Revisit condition: if a future profile value becomes
  hard-constraining or materially output-determining, snapshot-vs-live must be
  re-decided then.

## 13. Contract Boundaries

- **HTTP contract unchanged**: no new/changed request or response fields; OAS
  snapshot tests remain byte-identical.
- **No write API** in TM-213. The write surface belongs to the future Traveler
  Preference UC with Mobile.
- **No schema change / migration**: EF parity mapping only; `dotnet ef
  migrations add` is forbidden (AGENTS.md §3.1).

## 14. Handoff to the Future Traveler Preference UC

The implementing UC receives, as binding input:

1. The canonical persisted contract (§4) and field tiers (§5).
2. The precedence table (§7) and null/unspecified semantics (§8).
3. RiskTolerance willingness semantics and the actual-risk separation rule (§9).
4. The TravelerProfile write surface, write validation, and Mobile/profile form
   behavior are owned by that UC, including these policy decisions:
   - whether tags must reference existing `catalog.Tags` names at write time;
   - enforcement of the 1000-character limit on JSON fields (read path makes
     no length decision — §11).
5. Production-data rule: the current application code has no discovered
   production path to create/update TravelerProfile rows; test/dev fixtures may
   create rows; production code contains no mock/seed path; no claim is made
   about actual production database contents (consistent with §10).

## 15. Test/Dev Mock-Seed Strategy

- Application unit tests create TravelerProfile rows through their existing
  local fixture/context pattern via `TravelerProfile.Create(...)` +
  `dbContext.TravelerProfiles.Add(...)`; no additional profile fixture helper
  is introduced.
- SQL Server integration tests seed rows through that project's own existing
  fixture/context pattern (`SqlServerTestDatabase`, `CreateDbContext()`, local
  `SeedAsync` raw-SQL seeding); no test-project-to-test-project reference is
  introduced. The real SQL round-trip case (raw INSERT → `ApplicationDbContext`
  materialization → assert all six fields) lives in this integration layer,
  not in the Infrastructure persistence-model tests, which stay EF-metadata-only.
- Production independence: no seed/mock path anywhere in `src`; production code
  cannot distinguish test-seeded from UC-written rows; the row-absent case is
  the same code path as every "no tokens" case; no claim is made about actual
  production database contents.
- No `database/` artifact is created for TM-213: all files under `database/`
  must not change; unit/integration fixtures cover all test seeding.

## 16. Error Conditions

TM-213 introduces **no new error codes and no new failure paths**. Existing
`planning.constraints_infeasible` behavior is unchanged. Preference
degradation (§10/§11) never surfaces as an HTTP error.

## 17. Acceptance Criteria

1. Read model exposes all six fields mapped 1:1 to `dbo.TravelerProfiles`
   (types, nullability, enum names = DB CHECK values; `updated_at` via
   `.AsUtcDateTime2()`); the three enum-valued fields assert non-Unicode
   VARCHAR(20) parity — `HasMaxLength(20)` + `IsUnicode(false)` or an explicit
   `varchar(20)` column type; a persistence-model test asserts **schema parity
   only** — it asserts no behavior for Tier C/D fields.
2. Preference-token parsing is unit-tested for: row absent, null, empty,
   whitespace, malformed JSON — each safely degrades to no preference tokens;
   no test asserts length-based degradation; no read-path length branch exists.
3. Ranking behavior is pinned: matching category = +100, each matching tag =
   +100, ordering exactly as §6; a row-absent regression test proves output
   identical to current behavior.
4. Backward compatibility proven by the full existing suite remaining green
   with no new failure paths and no HTTP contract/OAS change.
5. `request_hash` contains no profile fields, proven observably: the same
   IdempotencyKey + payload replays the original itinerary even when the
   profile row is added, changed, or removed between attempts.
6. RiskTolerance: contract documentation present in the handoff package (§14);
   zero scheduling consumption, weather integration, or risk-scoring code in
   TM-213.
7. No invented system defaults: TM-213 adds no substitution for null
   TravelPace/RiskTolerance; the only system defaults remain the evidenced
   TransportMode Walking and RestPreference Auto.
8. No write API, no schema change, no EF migration; no `database/` artifact is
   created and all files under `database/` remain unchanged.
9. No magic strings: enum values and normalization constants referenced from
   named domain constants (AGENTS.md §4.1); AsNoTracking retained for the
   profile read.
10. Replay semantics documented in code where the profile is read (live-read;
    replay returns the original itinerary; new key for fresh preferences).

## 18. Verification

- Full suite: `dotnet test` (solution `TripMate.slnx`) from the repository
  root — green before implementation starts (baseline gate) and after every
  task.
- Baseline gate definition: implementation starts only from the verified
  TM-213 baseline state — expected HEAD, no tracked `src/`/`tests/` diff, and
  only the two expected untracked Step-3 artifacts (this spec and its plan)
  present; any other unexpected change blocks implementation. A literally
  empty `git status` is not required.
- No new warning-free violations of AGENTS.md §2/§3/§4 rules.
