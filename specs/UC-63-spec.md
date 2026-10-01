# UC-63 View Payout Records Backend Specification

## Status

**Approved 2026-10-01** — rewritten against SRS §3.9.9.1 (detailed section read directly from
`Report3_Software-Requirement-Specification (2).docx`) after the developer rejected the first
draft's invented summary/columns. Decisions D1–D10 below were approved by the developer on
2026-10-01. Implementation follows `plans/UC-63-plan.md`.

## Sources

- SRS §3.9.9.1 "View Payout Records" — screen **Payout Settlement** on the Next.js web
  administration application; purpose: review payout requests submitted by Tour Operators so
  settlements of a closed period can be processed in controlled order.
- Section business rules (cited with their §3.9.9.1 meanings): **BR-115** (Administrator role
  only), **BR-112** (net payout = gross revenue − platform commission), **BR-114** (commission
  rate is the rate configured for the settlement period of the record), **BR-111** (a payout
  record exists only for a closed settlement period containing completed tours), **BR-79**
  (every monetary value is presented in VND), **BR-52** (list retrieved on submit, CR-01
  pagination). Note: these section-local numbers collide with the SRS appendix's consolidated
  BR list (appendix BR-111/112/114 describe other domains). Following the repo's documented-
  conflict convention, this spec cites the §3.9.9.1 meanings and records the numbering
  collision instead of rewriting the SRS.
- CR-01 (pagination), CR-07 (`Asia/Ho_Chi_Minh` timestamps; requested date displayed
  `dd/MM/yyyy`).
- Locked messages (SRS §5.3): MSG126 (authorization), MSG127 (system), MSG128 (no records).
  MSG110/112/113 belong to UC-46/UC-65 flows and are not used here.
- Schema truth: `payment.Payouts`, `payment.PayoutItems` (deferred to UC-64),
  `dbo.OperatorProfiles`, `dbo.Users`.

## Facts vs approved decisions

| # | Item | Type | Resolution |
| --- | --- | --- | --- |
| D1 | Data source | Fact-derived | `payment.Payouts` rows returned **as persisted**. Amounts are produced by the backend payout engine (BR-112 holds at the DB level via CHECK `net = gross − commission`); UC-63 never recalculates, edits, or derives amounts (PC-02: no payout record is modified). Runtime payout generation is a separate dependency — tests seed records directly (UC-58 precedent). |
| D2 | Endpoint & scope | Approved | `GET /api/v1/admin/payouts` — one Administrator-only, read-only list endpoint with keyword, filters, deterministic ordering, pagination, and summary counters. **No detail route** — UC-64 owns details, UC-65 owns settlement actions. |
| D3 | Search & filters | Approved | One `keyword` (case-insensitive substring against the derived **Payout Code** or the operator **company name**; trimmed, max 200) plus filters: `status`, `periodFrom`, `periodTo`. |
| D3a | Status filter values | Approved (B1) | The filter accepts **all five** DB lifecycle values `Pending`, `Requested`, `Confirmed`, `Paid`, `Rejected` — a superset. The SRS screen spec lists only (Pending, Confirmed, Rejected); omitting `Requested`/`Paid` would make rows in those lifecycle states unfilterable, so the defect is recorded here rather than reproduced. |
| D3b | Settlement period filter | Approved | `periodFrom`/`periodTo` as ISO `yyyy-MM-dd` against `period_start`/`period_end`; `periodFrom ≤ periodTo`; no future-date restriction (historical periods are legitimate data). |
| D4 | Summary counters | Approved (B2) | Computed **over the filtered result** per SRS: `pendingRequests` = COUNT(status ∈ {Pending, Requested}); `totalRequestedAmount` = SUM(net_amount) over {Pending, Requested}; `totalConfirmedAmount` = SUM(net_amount) over {Confirmed, Paid} (Paid rows were confirmed before transfer). `Rejected` rows count toward neither amount. |
| D5 | Serialization | Approved | `grossRevenue`, `commissionAmount`, `netAmount`, `totalRequestedAmount`, `totalConfirmedAmount` serialize as JSON **numbers** (DECIMAL(14,2) fits JavaScript's safe range; BR-79: VND). IDs (`payoutId`, `operatorUserId`) serialize as **strings** (BIGINT safety, UC-58 convention). |
| D6 | Operator information | Approved | List column "Tour Operator" exposes `operator.companyName` and `operator.userId` only. `commissionRate` and the operator email are **excluded** — rate belongs to the UC-64 amount breakdown (§3.9.9.2). |
| D7 | Audit | Approved | No audit write. The SRS defines no access-audit rule for this read-only list (PC-02 only) and no location/personal-tracking data is exposed; the handler performs no `SaveChanges`. |
| D8 | Payout Code | Approved | Derived as `PO-{payout_id}` — invariant decimal session-free format, no locale formatting or padding, **no DB column added** (same precedent as UC-58's approved `TRIP-{session_id}`). |
| D9 | Requested Date | Approved | `requested_at` is part of the list response (nullable — a `Pending` payout has not been requested yet; the Web renders "Not available"). CR-07: displayed `dd/MM/yyyy` in `Asia/Ho_Chi_Minh`. |
| D10 | MSG134 for invalid period range | Approved (B4) | The SRS references **MSG29** for "the submitted settlement period selection is logically invalid", but locked MSG29 (§5.3) is the invalid-POI-coordinates message — a wrong reference that must never be displayed. Following the UC-69/MSG132 precedent: runtime code `payout.invalid_period_range`, **proposed MSG134** wording `"The submitted settlement period range is logically invalid."` — pending catalog approval; the SRS reference defect is recorded here. |

## Scope

One Administrator-only, read-only query endpoint. Adds the minimum read-only Domain entity and
EF mapping that does not yet exist: `Payout` → `payment.Payouts` (with the
`dbo.OperatorProfiles` relationship). `payment.PayoutItems` is **not mapped** (per-booking
breakdown belongs to UC-64). No SQL schema change, no `SaveChanges`, no mutation.

## API contract

### Endpoint

`GET /api/v1/admin/payouts`

- Missing/invalid authentication → `401`; valid non-Administrator → `403` (MSG126 at the Web
  boundary, BR-115); unexpected failure → `500` without database details. No `404` case (empty
  result is `200` with MSG128 semantics).

### Query parameters

| Parameter | Type | Default | Rules |
| --- | --- | --- | --- |
| `keyword` | string? | `null` | Trimmed, max 200; case-insensitive substring against derived Payout Code or operator company name (D3). |
| `status` | string? | `null` | One of the five lifecycle values (D3a). |
| `periodFrom` | date? | `null` | `yyyy-MM-dd`; matches payouts with `period_end ≥ periodFrom` (D3b). |
| `periodTo` | date? | `null` | `yyyy-MM-dd`; matches payouts with `period_start ≤ periodTo`; must not precede `periodFrom` → `400` with `payout.invalid_period_range` (MSG134 proposed, D10). |
| `pageNumber` | integer | `1` | Minimum 1. |
| `pageSize` | integer | 20 | 1–100 (CR-01). |

### Successful response (`200 OK`)

```json
{
  "summary": { "pendingRequests": 1, "totalRequestedAmount": 9000000.00, "totalConfirmedAmount": 437500.00 },
  "pageNumber": 1,
  "pageSize": 20,
  "totalCount": 2,
  "totalPages": 1,
  "items": [
    {
      "payoutId": "12",
      "payoutCode": "PO-12",
      "operator": { "userId": "2", "companyName": "Da Nang Tours" },
      "periodStart": "2026-08-01",
      "periodEnd": "2026-08-31",
      "grossRevenue": 10000000.00,
      "commissionAmount": 1000000.00,
      "netAmount": 9000000.00,
      "requestedAtUtc": "2026-09-01T02:30:00Z",
      "status": "Requested"
    },
    {
      "payoutId": "11",
      "payoutCode": "PO-11",
      "operator": { "userId": "3", "companyName": "Hoi An Walks" },
      "periodStart": "2026-07-01",
      "periodEnd": "2026-07-31",
      "grossRevenue": 500000.00,
      "commissionAmount": 62500.00,
      "netAmount": 437500.00,
      "requestedAtUtc": null,
      "status": "Confirmed"
    }
  ]
}
```

### Data mapping

- Ordering: `period_start DESC`, then `operator_user_id DESC`, then `payout_id DESC` — the
  newest closed settlement period leads the first page, matching the SRS normal flow's
  "latest closed settlement period" presentation without inventing a default filter.
- List columns follow the SRS screen spec order: Payout Code (D8), Tour Operator (D6),
  Settlement Period, Gross Revenue, Commission, Net Payout, Requested Date (D9), Status.
- `periodStart`/`periodEnd`: stored DATE values serialized as `yyyy-MM-dd` (calendar dates,
  not instants — no timezone conversion).
- `status`: the stored CHECK value, verbatim; the Web renders badge + text (never color alone).
- Filters compose (AND). An empty match is `200 OK` with `totalCount = 0`, `totalPages = 0`,
  `items = []` and zeroed summary; the Web renders MSG128. Summary counters reflect the
  filtered set (D4) and are recomputed on every page/filter submission (PC-03: filters remain
  applied while browsing pages).

## Acceptance criteria

1. Only Administrators can retrieve the endpoint; `401`/`403` follow the project ProblemDetails contract (BR-115).
2. Invalid `status`, oversized `keyword`, inverted `periodFrom > periodTo` (MSG134 proposed code, HTTP 400), non-positive `pageNumber`, or out-of-range `pageSize` return `400` with field errors.
3. The response follows the DTO above exactly: derived `payoutCode`, string IDs, numeric 2-decimal money, `yyyy-MM-dd` periods, nullable `requestedAtUtc`, and the three filtered summary counters (D4).
4. Filters compose; ordering is deterministic; pagination applies after filtering; summary reflects the filtered set on every request.
5. The handler is read-only (no `SaveChanges`, no amount recalculation — PC-02, BR-112 untouched).
6. Unit tests cover validation boundaries and summary formulas (including a `Rejected` row contributing to neither amount and a `Paid` row counting toward `totalConfirmedAmount`); API integration tests cover `401`, `403`, `400`, `200`, filtering, deterministic pagination, and empty results; SQL Server integration tests verify joins, summary aggregation, and tie-breaking against the real schema.
7. OpenAPI documents query values, response DTO, and authorization responses.

## Non-goals and dependencies

- No detail route (UC-64), no confirm/update-settlement actions (UC-65), no operator-side request flow (UC-46), no payout recalculation or regeneration.
- `payment.PayoutItems` mapping is deferred to UC-64; bank-account presentation is UC-64 scope.
- Runtime payout generation (BR-111: closed settlement period with completed tours) is an external dependency; this UC reads persisted rows only.
- Administrator login/session correctness is an external dependency (admin cookie contract).
