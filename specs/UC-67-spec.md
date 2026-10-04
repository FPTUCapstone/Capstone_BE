# UC-67 Export Statistical Reports Backend Specification

## Status

**Draft — NOT ready for implementation.** Rewritten 2026-10-01 after the leader's six-point
re-review. Four decisions (DEC-1…DEC-4 below) require explicit team approval before any task
in `plans/UC-67-plan.md` may start. This specification does not assume any of them.

## Sources

- SRS §3.9.11 "Export Statistical Reports" — screen **Platform Statistics**; two-step flow:
  generate the aggregation on screen, then export exactly that result (BR-110).
- Section business rules: BR-115 (role), BR-129 (closed period only), BR-110 (file mirrors the
  screen), BR-109 (revenue from Confirmed/Completed bookings, net of recorded refunds), BR-79
  (VND), BR-130 (export event audited).
- Locked messages §5.3: MSG01, MSG126, MSG127, MSG128 (locked text: "No records found matching
  your criteria."). Reference defects recorded in M1–M3.

## Team decisions required before implementation

| # | Decision | Options | Recommendation |
| --- | --- | --- | --- |
| DEC-1 | **Export formats** — SRS lists Excel, CSV, PDF. CSV is deliverable with no dependency; Excel/PDF require new NuGet packages (ClosedXML/EPPlus/QuestPDF — licensing and approval per engineering rules). | A: ship CSV only, Excel/PDF recorded as an approved follow-up; B: ship all three, choosing and approving a library now; C: ship CSV + one of Excel/PDF. | A — CSV now, formats follow-up. The FE renders the SRS format options exactly as the decision dictates (enabled or disabled with titles). Until DEC-1 is approved, no export format is an acceptance criterion. |
| DEC-2 | **Service bookings** — `commercial.ServiceBookings` exists but is unmapped; the SRS "Booking Type" filter implies it. | A: defer service bookings entirely and drop the Booking Type filter from this delivery (the filter returns with the follow-up); B: map `ServiceBookings` now and support the filter. | A — dropping the filter beats showing a `Service` option that answers "no data" while data exists (misleading-empty defect flagged in re-review). |
| DEC-3 | **Revenue population + period assignment** — BR-109 limits revenue to Confirmed/Completed bookings, but the schema has no `completed_at`; the only available assignment timestamp is `booked_at`. Refunds can also be recorded long after the period closes. | Population fixed to bookings with `status ∈ {Confirmed, Completed}` at query time, assigned to the period by `booked_at` (Vietnam local time, D3). Recorded limitation: a booking's refund may be processed after the period closes and **will** change the reported net figure on re-export — inherent to the schema until a completion/snapshot timestamp exists. | Approve this formula and its documented mutability caveat. |
| DEC-4 | **Generate → Export consistency (PC-02)** — identical filters do not guarantee identical numbers if refund data changes between the two calls. | A: **signed result token** — generate returns the figures plus an encrypted, time-limited token (ASP.NET Data Protection, purpose-scoped, TTL 30 minutes); export accepts only the token, decrypts, formats the CSV **from the token's figures** (exact PC-02), audits. Tampered payload → 400; expired token → 422 `statistics.result_expired` (proposed MSG138: "The generated report has expired. Please generate the report again."). B: server-side snapshot cache — stateful, breaks with multiple instances. C: recompute and accept drift — violates BR-110. | A — stateless, tamper-proof, exact. |

## Resolved re-review points (incorporated, no longer open)

| # | Point | Resolution in this document |
| --- | --- | --- |
| R1 | Format scope reduction needs team approval | Moved out of acceptance criteria into DEC-1; status blocks implementation. |
| R2 | Revenue formula missing BR-109 conditions | D2 rewritten: Confirmed/Completed population, `booked_at` assignment (DEC-3), `total_amount`-based gross (no multi-transaction double counting — `PaymentTransactions` is no longer part of the formula and is not mapped by this UC). |
| R3 | Generate/Export consistency | D9/DEC-4: signed result token; export never recomputes. |
| R4 | Misleading filters | Booking Type filter removed (DEC-2 default); Tour Operator and Destination filters are **company-name / destination-name keyword** inputs (no ID-select endpoints exist on this baseline — an operator/destination list endpoint would be separate scope). |
| R5 | Period contract loose | D3: `periodStart`/`periodEnd` must align exactly to a whole month/quarter/year per `periodType` (misalignment → 400 field error); UTC timestamps are bucketed by their **Asia/Ho_Chi_Minh local time** (CR-07, UTC+7); unified bucket label rule (D11); per-type bucket/summary/CSV columns fully specified below. |
| R6 | Messages and approval evidence | M1–M3 below (MSG149 non-existent, MSG117/MSG29 wrong references, MSG128 locked text used verbatim); baseline evidence recorded with SHA/commands/results (see end); the Stitch waiver sentence is removed — the Stitch decision is **pending explicit maintainer approval**, not waived by spec approval. |

## Message reference defects

- **M1:** SRS MSG149 ("file generated and delivered") does not exist — the locked catalog ends
  at MSG130. Proposal: **MSG136** "Statistical report exported successfully." (pending catalog
  approval).
- **M2:** SRS MSG117 ("the file cannot be generated") — locked MSG117 is the UC-57 success
  toast; never displayed here. Proposal: **MSG137** "The report file could not be generated.
  Please try again." — however, under DEC-4 the export generates from the token, so generation
  failure collapses into token expiry (`statistics.result_expired`, proposed **MSG138**) or the
  audit 503 path; MSG137 is recorded only if a non-token failure mode survives review.
- **M3:** SRS MSG29 ("the selected period is not yet closed") — locked MSG29 is the
  POI-coordinates message. Proposal: `statistics.period_not_closed` (422) with neutral wording
  "The selected period is not closed yet. Only closed months, quarters, and years can be
  exported." (pending catalog approval).

## Scope

Two Administrator-only endpoints under `/api/v1/admin/statistics` plus one streamed CSV export
(gated by DEC-1). Adds the minimum read-only mappings missing on this baseline: `Booking` →
`commerce.Bookings` and `Refund` → `payment.Refunds` (shapes proven on the payout-details
branch). `User`, `OperatorProfile`, `Tour`, `TourSchedule`, `TourDestination`, `Destination`
are already mapped. **No SQL schema change.** The only write is the BR-130 export audit row.

## Report definitions (per type — buckets, summary, CSV columns)

Common period rules (D3/R5): `periodStart`/`periodEnd` must align exactly to the selected
`periodType` (Month: first–last day of one month; Quarter: first day of `Q<n>` – last day;
Year: Jan 1 – Dec 31; misalignment → `400` field error). A record belongs to a bucket by its
**Vietnam local time** (`created_at`/`booked_at` converted +07, CR-07). Bucket labels:
Month → `MM/yyyy`, Quarter → `Q<n>/yyyy`, Year → `yyyy`; every bucket also carries
`bucketStart` (`yyyy-MM-dd`) as the sort key. Buckets with zero activity are **omitted**
(MSG128 governs when the whole result is empty).

| Report type | Bucket/row fields | Summary | CSV columns |
| --- | --- | --- | --- |
| User Growth | `{ bucketLabel, bucketStart, newUsers }` — count of users whose Vietnam-local `created_at` falls in the bucket | `{ totalNewUsers }` | `Period,Bucket,New Users` |
| Booking Volume | `{ bucketLabel, bucketStart, tourBookings }` — count of Bookings (any status) per `booked_at` bucket; DEC-2 removes the type split | `{ totalTourBookings }` | `Period,Bucket,Bookings` |
| Platform Revenue | `{ bucketLabel, bucketStart, grossAmount, refundedAmount, netAmount }` — DEC-3 population: Bookings `status ∈ {Confirmed, Completed}` by `booked_at`; `grossAmount` = SUM(`total_amount`); `refundedAmount` = SUM(`Refunds.amount`, `status='Processed'`) of those bookings; `netAmount` = gross − refunded (BR-109/BR-79) | `{ totalGross, totalRefunded, totalNet }` | `Period,Bucket,Gross (VND),Refunded (VND),Net (VND)` |
| Tour Operator Performance | Rows, not time buckets: `{ operator { userId, companyName }, bookings, grossAmount, refundedAmount, netAmount }` over the whole period, ordered by `netAmount` desc then company name (filters: operator keyword narrows rows) | `{ operatorCount, totalNet }` | `Period,Operator,Bookings,Gross (VND),Refunded (VND),Net (VND)` |
| Destination Popularity | Rows: `{ destination { destinationId, name }, bookings }` — bookings mapped through Bookings → TourSchedules → TourDestinations → Destinations, ordered by `bookings` desc then name; destinations with zero bookings omitted; empty result → MSG128 | `{ destinationCount, totalBookings }` | `Period,Destination,Bookings` |

## API contract

### Endpoints

`POST /api/v1/admin/statistics/reports/generate` — body
`{ reportType, periodType, periodStart, periodEnd, operatorKeyword?, destinationKeyword? }`
(field names final; filters only for the types that accept them — wrong filter for a type →
`400` field error).

- `200` — figures per the table above **plus `resultToken`** (DEC-4: encrypted payload =
  criteria + figures + `generatedAtUtc`, TTL 30 minutes).
- `400` — missing report type/period (locked MSG01), misaligned period, filter applicable to
  the type.
- `422` — open period → `statistics.period_not_closed` (M3); expired/invalid token on export →
  `statistics.result_expired` (MSG138 proposed).
- `401/403` — MSG126 (BR-115). Empty result → `200` with zero rows/empty buckets (Web renders
  MSG128). Aggregation failure → `503` MSG127 (SRS 4.a2).

`POST /api/v1/admin/statistics/reports/export` — body `{ resultToken, format }` where
`format` per DEC-1 (only `csv` until Excel/PDF are approved).

- `200` — `text/csv` stream with `Content-Disposition: attachment; filename=<report>_<period>_<generatedAt>.csv`
  built **from the token's figures** (BR-110/PC-02 exact) + one BR-130 audit row
  (`StatisticalReportExported` / `StatisticalReport`, `afterData` JSON: `reportType`,
  `periodType`, `periodStart`, `periodEnd`, `format`, filters — PC-04).
- Token expired → `422 statistics.result_expired`; token invalid/tampered → `400`;
  audit failure → `503 admin.statistics_audit_unavailable` (MSG127 text, fail-closed, recorded
  as `audit.database_write_failed` by the failure behaviour — UC-57 CI lesson).

## Acceptance criteria (draft — finalized after DEC-1…DEC-4)

1. Only Administrators reach the endpoints; `401`/`403` per the ProblemDetails contract.
2. Period alignment and closure enforced exactly per D3; MSG01 for missing selections.
3. Each report type aggregates per its table above (BR-109 population and formula; bucketing
   by Vietnam local time; unified labels).
4. Export formats exactly per DEC-1; the CSV (or approved formats) is generated from the
   token's figures — byte-identical numbers to the displayed result (PC-02) — and audited
   exactly once (BR-130/PC-04).
5. Aggregation is read-only (PC-03); the only write is the export audit row.
6. Unit tests: period alignment/closure boundaries (last closed month/quarter/year; first open
   one), bucket edges across Vietnam-midnight conversions, per-type aggregation formulas, CSV
   shape per type, token round-trip, expiry, tamper rejection, audit fail-closed. API tests:
   auth, validation, empty, export headers. SQL tests: aggregations vs the real schema and the
   audit row — **run locally with a real SQL Server before every push** (UC-57 CI lesson).

## Non-goals

Excel/PDF and any NuGet addition until DEC-1; service bookings and the Booking Type filter
until DEC-2; rerouting-incident reports; scheduled/email reports; chart libraries (the FE
chart is CSS-based); any mutation other than the export audit row.
