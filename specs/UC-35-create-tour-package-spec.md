# UC-35 Create Tour Package — production baseline

## Product outcome

An approved, active Tour Operator creates a commercial tour package that can
progress safely from an editable Draft to a customer-visible listing. A package
is a reusable product template; dated departures, pricing, capacity and selling
terms belong to it. A customer must never be able to book a Draft, rejected or
unpublished package.

## Scope

This UC delivers the operator-facing package authoring foundation:

- Create, retrieve, update and list the caller's Draft packages.
- Model package basics, category, destinations, day-by-day POI itinerary,
  inclusions, exclusions, traveller requirements and operator notes.
- Model one or more dated departures with a meeting point, start/end time,
  minimum and maximum capacity, registration deadline, and adult/child/infant
  selling prices.
- Select a configurable cancellation-policy template and snapshot its rules
  onto the package so later template changes do not alter an already published
  offer.
- Compute server-side itinerary warnings for duplicate POIs, a planned visit
  outside known POI opening hours, and impossible ordering where enough route
  data exists. Warnings do not block a Draft save; blocking errors do block a
  review submission.
- Reuse the existing authenticated Cloudinary media flow after the first Draft
  save. At least one active primary image is required before review submission.
- Support `Draft -> PendingReview`, `PendingReview -> Approved | Rejected`,
  and `Approved -> Published` lifecycle transitions. The review/publish actions
  are protected endpoints, even if their operator/admin UI lands in later UCs.
- Create immutable audit events for creation, meaningful edits and state
  transitions. The create request supports an idempotency key.

Not in this UC: live supplier inventory, payment capture, dynamic pricing,
room allocation, booking/cancellation execution, or an automatic promise that
all route estimates are available. Missing routing/opening-hours data produces
an explicit warning rather than a fabricated estimate.

## Business decisions approved for the baseline

| Topic | Rule |
|---|---|
| Product type | Scheduled departures first; custom/private tours are a later product type. |
| Pricing | Adult price is mandatory. Child and infant prices are optional. Single-room and holiday supplements are deferred, not represented by misleading fields. |
| Capacity | A departure has `minimumPax`, `maximumPax` and a registration deadline before its start. |
| Pricing control | Operators submit selling prices; later costing/margin uses a separate internal-cost model. |
| Feasibility | Saving a Draft is permissive with warnings. Requesting review is strict: all blocking validations must pass. |
| Cancellation | A package references a policy template; ordered cutoff rules are copied to the package at selection time. |
| Publish state | Only `Published` packages are public/bookable. `Approved` is reviewed but still not public. |
| Time | API timestamps are UTC; the package stores an IANA time-zone identifier for display and feasibility evaluation. |

Default cancellation template seeded by operations (configurable, not hard-coded
in application code): free cancellation at least 30 days before departure; 25%
fee at 15–29 days; 50% at 7–14 days; 100% below 7 days. Legal/operations owns
the final values before production rollout.

## API contract

All endpoints require the `TourOperator` role and a caller with an active user
account plus an approved operator profile.

- `POST /api/v1/operator/tours` creates a Draft. `Idempotency-Key` is required;
  replaying the same key and payload returns the original resource, while the
  same key with another payload returns a conflict.
- `GET /api/v1/operator/tours?status=&page=&pageSize=` lists only the caller's
  packages.
- `GET /api/v1/operator/tours/{tourId}` returns only the caller's package,
  including departures, itinerary, selling terms, media summary and warnings.
- `PUT /api/v1/operator/tours/{tourId}` replaces editable Draft data. It is
  rejected after `PendingReview`.
- `POST /api/v1/operator/tours/{tourId}/submit-review` validates sellability
  and transitions `Draft` to `PendingReview`.

The create/update payload contains: `title`, `description`, `categoryId`,
`destinationIds`, `timeZoneId`, `durationDays`, `highlights`, `inclusions`,
`exclusions`, `travellerRequirements`, `cancellationPolicyTemplateId` (but see
Amendment A: `timeZoneId`, `highlights`, `travellerRequirements` are deferred), ordered
itinerary items, and departures. A departure has UTC start/end, meeting point,
minimum/maximum capacity, registration deadline and prices.

Responses return a stable DTO rather than EF entities and contain
`id`, `status`, `warnings`, `canSubmitForReview` and the authoritative data.
Failures use the standard ProblemDetails mapping.

## Persistence and data ownership

The existing `commerce.Tours`, `TourSchedules`, `TourItineraryItems`,
`TourDestinations` and `TourMedia` tables remain the source of truth where they
fit. A database-first, idempotent SQL migration adds only the missing package
authoring fields/tables: terms, departure pricing and minimum capacity,
cancellation templates and rule snapshots, idempotency operations and audit
events. It must preserve current data and constraints.

No EF migrations are generated. All new `datetime2` fields use the existing UTC
mapping convention. Same-transaction child rows are connected through navigation
properties, never a not-yet-generated scalar parent ID.

## Domain rules and errors

- A title is required and at most 200 characters; description is required.
- A package has at least one destination, itinerary item and future departure.
- Day and sequence are unique within a package; each day is within
  `durationDays`; POIs and destinations must exist and be active.
- All selling prices are non-negative and have at most two decimal places;
  adult price is positive; child/infant price may be null.
- `1 <= minimumPax <= maximumPax`; reserved capacity never exceeds maximum;
  registration deadline is before departure start; departure end is after start.
- A selected cancellation policy must be active and its snapshot has ordered,
  non-overlapping cutoffs and fee percentages in `[0, 100]`.
- A Draft is editable only by its owner. A non-Draft update is a conflict.
- A request for review requires an active primary image, complete selling terms,
  a cancellation-policy snapshot, no blocking validation errors and at least one
  future departure.
- The server does not trust a client-provided operator ID, warnings or status.

Expected application errors include access denied, invalid lifecycle transition,
not found/inactive category-destination-POI-policy, idempotency conflict,
editing locked package, and review prerequisites missing.

## Mobile acceptance criteria

The Mobile operator flow uses the feature-first Page -> Cubit -> Use Case ->
Repository -> Data Source contract. It provides: list/resume Draft; an
autosaving, accessible multi-step editor; catalog POI/destination selection;
dynamic ordered itinerary and departures; visible server warnings; policy
selection; media management through the existing media flow; a customer-style
preview; safe loading/error/retry states; and explicit review-submission
confirmation. It never exposes raw backend failures or treats a local Draft as
success when the server save failed.

## Acceptance tests

1. An approved operator creates a Draft once when a create request is replayed
   with its original idempotency key.
2. Another operator cannot read or edit that Draft.
3. The API persists destinations, itinerary, departure pricing/capacity and a
   cancellation-policy snapshot atomically.
4. An infeasible Draft returns warnings, while a review submission with a
   blocking problem is rejected and stays Draft.
5. A Draft without a primary image or valid future departure cannot be submitted
   for review.
6. A package can never become public/bookable before the published lifecycle
   state.
7. Mobile renders server validation failures and warnings, supports resuming an
   existing Draft, and passes Cubit/widget tests.

---

## Amendment A (2026-10-16) — close the gaps against SRS §3.8.2.1 / BR-101

Status: **APPROVED by the developer on 2026-10-16 — Core first, Extras later.**

### A.1 Decisions already approved by the developer

| Topic | Decision |
|---|---|
| Cancellation policy | Keep the **template + rule snapshot** model. SRS §3.8.2.1 describes a rich-text field; this is a recorded deviation. |
| Price / capacity | SRS "Price per Participant" = `Tour.BasePrice`. Adult/child/infant prices and capacity live on each departure. A client's "Max capacity" field is only the default capacity for a newly added departure. |
| BR-103 versioning | Out of scope for UC-35; belongs to UC-36. Update stays `Draft`/`Rejected` only; any other status returns `operator_tour.edit_locked`. |
| Reviewer role | BR-56 names an Administrator; `AdminToursController` keeps `Administrator`. |
| Platforms | WEB_SCOPE_MATRIX lists UC-35 as `SHARED_WEB_MOBILE`: Web and Mobile are both required. Delivery order: BE, then Web, then Mobile. |

### A.2 Current state that the earlier spec text did not reflect

- `commerce.Tours` already has `time_zone_id`, `highlights`, `inclusions`,
  `exclusions`, `traveller_requirements`, `cancellation_policy_template_id`, and
  `Tour` maps them, but no command, handler or DTO reads or writes them.
- `commerce.TourDestinations` and `Tour.Destinations` exist, but create/update do
  not write destinations (UC-24 search filters by them, so a tour without any
  destination is not discoverable by region).
- The cancellation snapshot is only written by the separate
  `PUT /operator/tours/{id}/cancellation-policy/{templateId}` call.
- The status enum value is `Pending` (DB check, code, Web). The spec's
  `PendingReview` is renamed to `Pending`.
- `warnings` / `canSubmitForReview` / `timeZoneId` / `highlights` appear in the
  contract text above but not in code.
- `commerce.Tours` has `CK_Tours_BasePriceWholeVnd (base_price = FLOOR(base_price))`,
  but the validator accepts two decimals, so a non-whole price fails at the
  database as a 500 instead of a 400.
- The migration seeds the default policy fee for 15–29 days as 15%; this spec
  (above) says 25%.

### A.3 Contract changes

**Destination catalogue** *(decision: route)* — `GET /api/v1/destinations`,
anonymous like `tour-categories`, `Cache-Control: no-store`, returns
`[{ "destinationId": long, "name": string }]` ordered by name.

**Create / Update payload** gains:

| Field | Rule |
|---|---|
| `destinationIds` | required, 1–10 unique ids of existing destinations; order = `sequence_no` (1..n). Unknown id → `operator_tour.destination_not_found`. |
| `cancellationPolicyTemplateId` | required (BR-101). Must be an active template with ≥1 rule; its rules are snapshotted into `TourCancellationPolicyRules` in the same transaction. Else `operator_tour.cancellation_policy_unavailable`. |
| `inclusions`, `exclusions` | optional, max 4000 chars each; trimmed, blank → null. |

`basePrice` and every departure `adultPrice/childPrice/infantPrice` must be a
**whole VND amount** *(decision)* to match `CK_Tours_BasePriceWholeVnd` and BR-79.
`categoryId` stays optional.

**Deferred out of UC-35 (approved 2026-10-16):** `timeZoneId`, `highlights` and
`travellerRequirements` are not added to any command or DTO; the existing
columns keep their database defaults.

The `Idempotency-Key` fingerprint includes all new fields. Update replaces
destinations, terms and the policy snapshot atomically with the other children.
The separate `PUT .../cancellation-policy/{id}` endpoint is kept unchanged.

**Detail DTO (`GET /operator/tours/{id}`)** gains `destinations[{destinationId,name}]`,
`inclusions`, `exclusions`, `cancellationPolicyTemplateId`,
`cancellationPolicyRules[{daysBeforeDeparture, cancellationFeePercent}]` and
`rejectionReason` (Core). *(Extras, only after Core is complete and green:)*
`mediaCount`, `hasPrimaryImage`, and `canSubmitForReview` + `reviewBlockers[]` (stable codes:
`primary_image_missing`, `future_departure_missing`, `cancellation_policy_missing`,
`destination_missing`), computed by the same rules `submit-review` enforces.
`submit-review` additionally requires at least one destination (Core) and keeps
requiring the cancellation-policy snapshot.

**List (`GET /operator/tours`)** *(decision)* gains `status`, `page`, `pageSize`
(default 20, max 100) and returns `{ items, page, pageSize, totalCount }`.
Invalid `status` or paging → 400.

**Server warnings** *(decision: defer)*: duplicate-POI / opening-hours / ordering
warnings need route and opening-hours data that is not available; they move to a
follow-up. The contract text above stays as the target, but UC-35 acceptance
test 4 is limited to blocking errors.

**Default policy seed**: change the 15-day rule fee from 15 to 25 *(decision:
align to this spec; operations may still change it)*.

### A.4 Error mapping for clients (SRS messages)

| SRS | Server signal |
|---|---|
| MSG01 mandatory field empty | 400 ValidationProblem on the missing property |
| MSG143 price/capacity not positive | 400 on `basePrice` / departure price / capacity |
| MSG29 departure in past / return before departure | 400 on departure `startAtUtc` / `endAtUtc` |
| MSG19 image invalid | existing media endpoints |
| MSG126 not an approved operator | 403 `operator_tour.access_required` |
| MSG127 cannot save | 5xx / network |
| MSG108 created | 201 |

### A.5 Added acceptance tests

8. Create persists destinations in the given order, terms, time zone and a
   policy snapshot in one transaction; the detail DTO returns them.
9. Create without a destination, or without a policy template, is a 400 and no
   row is written.
10. A fractional `basePrice` is a 400, not a database error.
11. Update replaces destinations/terms/policy snapshot; an update of a `Pending`
    tour is `operator_tour.edit_locked`.
12. `canSubmitForReview` is false with the matching blocker codes until image,
    future departure, policy and destination exist; `submit-review` agrees.
13. Replaying a create with the same key but a different destination list is
    `idempotency_key_payload_mismatch`.
14. `GET /destinations` returns the catalogue without authentication.

### A.6 Implementation constraints (from review of the amendment)

Each item was checked against the code on this branch. Items 1–2 and 4 are real
risks; item 3 and 5 are accepted as written.

1. **Replace-all children must be proven against SQL Server, not InMemory.**
   Application unit tests use the EF InMemory provider, which ignores unique
   indexes and delete/insert ordering, so the existing `PUT` replace path has
   never been exercised against a real database. Two concrete hazards:
   - `TourDestination` has composite key `(tour_id, destination_id)` plus unique
     `(tour_id, sequence_no)`. Removing and re-adding the same destination in one
     unit of work can fail at tracking time, and reordering `[1,2]` to `[2,1]`
     can violate the sequence index.
   - `TourItineraryItems` (`tour_id, day_no, sequence_no`) and
     `TourCancellationPolicyRules` (`tour_id, days_before_departure`) have unique
     indexes with the same delete-then-insert hazard.
   Rule: inside the existing serializable transaction, **delete the old children,
   `SaveChanges`, then insert the new children, `SaveChanges`**. Nothing is
   visible to other readers until commit. A SQL Server integration test must
   update with an identical, a reordered and a changed destination/itinerary list.
2. **One snapshot implementation.** Add a `Tour.CancellationPolicyRules`
   navigation and a single domain method (`ApplyCancellationPolicySnapshot`)
   that Create, Update and `PUT .../cancellation-policy/{id}` all call. Child rows
   are linked by navigation property, never by a scalar `TourId` (AGENTS §3.2).
3. **Idempotency fingerprint.** Normalize first (trim; blank → null for
   inclusions, exclusions), then hash.
   `destinationIds` and itinerary order are **not** sorted before hashing, so
   `[1,2]` and `[2,1]` with the same key return
   `idempotency_key_payload_mismatch` (acceptance test 13).
4. **One readiness evaluator.** A single pure
   `TourReviewReadinessEvaluator.Evaluate(tour state, hasPrimaryImage, nowUtc)`
   produces the blocker codes and is used by both the detail query and
   `submit-review`; the current time comes from `IDateTimeProvider`.
5. **Whole VND.** The check is `price == decimal.Truncate(price)` (scale is
   irrelevant: `500000.00` is valid, `500000.5` is 400). Time-zone validation is
   not needed because `timeZoneId` is deferred.

### A.7 Scope tiers (approved 2026-10-16)

**Core — implemented now**
destination catalogue; `destinationIds` and `cancellationPolicyTemplateId` on
create/update; `inclusions` / `exclusions`; whole-VND validation; detail DTO
returns destinations, terms, policy snapshot and `rejectionReason`;
`submit-review` requires ≥1 destination and a policy snapshot; fingerprint
normalization; shared snapshot method; seed 25 %; CI format fix; SQL Server
integration tests for create → get and replace-all.

**Extras — only after Core is complete and every test is green**
list `status`/paging; `canSubmitForReview` / `reviewBlockers` /
`hasPrimaryImage` / `mediaCount` with the shared readiness evaluator.

**Deferred out of UC-35**
`timeZoneId`, `highlights`, `travellerRequirements`.

### A.8 Notes for Web and Mobile (after BE)

- Editing is allowed for `Draft` **and** `Rejected`; show `rejectionReason` on a
  rejected tour.
- A "Max capacity" input on the general form is only the default capacity
  pre-filled when the operator adds a departure; it is not sent as a tour field.

### A.9 Defect found while implementing Core (fixed)

The first version of the migration added `commerce.TourSchedules.minimum_capacity`
as `NOT NULL` with no default. Every existing raw-SQL schedule writer (other use
case tests and seeds) then failed with "Cannot insert the value NULL into column
'minimum_capacity'", which broke tour search, trip review and review-context SQL
Server tests on this branch (8 failing tests that pass on `develop`). The
migration now adds `DEFAULT 1` (idempotent, also repairing databases that already
applied the first version), covered by
`LegacyScheduleInsert_WithoutMinimumCapacity_DefaultsToOne`.

---

## Amendment B (2026-10-16) — Extras and deferred items delivered

Status: **IMPLEMENTED on the UC-35 PRs (BE #82, Web #84, Mobile #44).** This supersedes the
"Extras" and "Deferred" tiers of Amendment A.7.

### B.1 Backend
- `GET /operator/tours` accepts `status` (enum name, case-insensitive), `page` (>= 1) and
  `pageSize` (1-100, default 20) and returns `{ items, page, pageSize, totalCount }`; an invalid
  status or paging value is a 400. Items carry the `destination` summary.
- `TourReviewReadinessEvaluator` is the single source of the review prerequisites. The detail
  query returns `mediaCount`, `hasPrimaryImage`, `canSubmitForReview` (editable status and no
  blocker) and `reviewBlockers[]`; `submit-review` uses the same evaluator.
- `timeZoneId` (IANA, validated, default `Asia/Ho_Chi_Minh`), `highlights` and
  `travellerRequirements` (max 4000 characters) are accepted by create/update, included in the
  idempotency fingerprint, stored and returned by the detail query. Update is a full replace, so
  clients must send the values they loaded.
- Detail `warnings[]` (never blocking): `duplicate_poi`, `poi_closed_on_day`,
  `outside_opening_hours`, `opening_hours_unknown`. Opening hours are judged on the date of the
  itinerary day (first scheduled departure in the package time zone + day offset) using the stored
  POI opening hours; missing data yields `opening_hours_unknown`.
- **Still not provided:** route/travel-time based ordering warnings. No routing data is stored
  for tours, and the spec forbids fabricating estimates.
- Create retries a bounded number of times on deadlock/unique-index collisions so concurrent
  duplicates replay the original draft (acceptance: no 500 for simultaneous duplicates).

### B.2 Clients
- Web and Mobile list, create, edit/resume, upload and manage cover photos, see server blockers and
  warnings, preview the package and submit for review. Rejected packages show the reason.
- Mobile does not keep a local copy of an unsent form: drafts live on the server, and a local copy
  would be visible to the next operator who signs in on a shared device.
