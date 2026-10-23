# TM-70 Phase 2 implementation boundaries

Base: `bdb70d8a74002dc5c99f124c7dfa46f4a648c82b`.
Foundation parent: `a47bfac9e004769b06dbcfa196da5ccebbb9203d`.
This branch is explicitly stacked on `feature/tm-70-search-contract-foundation`.

## Runtime surface

The only new live endpoint is anonymous `GET /api/v1/tour-categories`:
direct array of `categoryId`, `code`, `name`, active only, English name/code
ascending, `Cache-Control: no-store`. An empty server taxonomy returns `[]`.
No schema, seed, backfill or NOT NULL enforcement is added. ApplicationDbContext
already owns the concrete taxonomy DbSet; the application abstraction now exposes
it. Existing category columns use `Latin1_General_100_CI_AS`, so category codes
retain the schema's case-insensitive comparison; no new casing rule is invented.

Production `/api/v1/tours`, its legacy `departureDate`, public predicates,
no-store and response remain unchanged. The new V2 parser/request/validator/DTO
are prepared infrastructure, not a new public contract. No feature query parameter
or runtime configuration flag is added. The independent reader is not registered
and does not implement MediatR's request handler interface.

## Acceptance coverage and gates

| Task | State | Evidence / remaining dependency |
|---|---|---|
| 2.1 request parser | Infrastructure implemented | 12 canonical keys, ordinal key/duplicate rejection, NFC normalization, independent dates, dynamic default sort; no route activation |
| 2.2 validation | Infrastructure implemented | Whole-VND representation boundaries, MSG157/158 and existing field-linked ProblemDetails middleware, positive duration, rating scale, active taxonomy, paging, strict decimal representation |
| 2.3 keyword/relevance | BLOCKED | Frozen all-token contract not replaced; semantic Change Request pending |
| 2.4 dates | Independent SQL slice implemented | One UTC clock snapshot, inclusive Vietnam dates, upcoming equality, departure/identity tie-break, explicit past/Completed capacity preserved; no invented Disabled lifecycle |
| 2.5 duration/category | Independent SQL slice implemented | Exact duration, active code filter and nullable legacy categories; TC-D1..TC-D9 remain pending |
| 2.6 rating | BLOCKED | No unmerged PR72 migration or legacy Review aggregate copied; shared Review/POI eligibility pending |
| 2.7 sort | Partial | Price asc/desc and departure with Title/TourId ties implemented in independent slice; relevance/rating hard rejected |
| 2.8 DTO/availability | Partial | Canonical nullable serialization prepared; independent rows preserve schedule identity on unknown capacity and only ToContract(explicit approvedDisplayAggregateRating) supplies future approved aggregate; no aggregate inferred |
| 2.9 taxonomy | Implemented | Anonymous HTTP empty/active/order tests and SQL ordering test; no fabricated production taxonomy |

The independent reader returns candidates, not complete UC-24 results. It rejects
keyword/minimumRating/relevance/rating requests. Its ToContract method requires an
explicit nullable one-decimal display aggregate supplied by a future approved eligibility implementation;
null in this method is not evidence that legacy Reviews are eligible or ineligible.
Raw multi-decimal means are rejected: no midpoint-rounding policy is invented while
the shared eligibility/display contract remains unresolved.
Capacity is read from the same SQL snapshot as the representative schedule. Invalid
capacity yields unknown while retaining known schedule ID/time; there is no new
external availability provider or invented booking flag. A whole SQL provider outage
continues to fail the request through existing middleware, not fabricate a page.

Valid pages past totalPages remain empty with requested metadata, including offsets
past int.MaxValue. If a real database ever has enough matches that an in-range page
needs an offset above int.MaxValue, this isolated reader explicitly rejects that
unimplemented EF paging adapter case; V2 runtime acceptance remains blocked.

## TDD evidence

Clean baseline confirmed by parent: 3,193 passed, 0 failed, 391 explicit optional
integration skips before implementation. Foundation RED: missing V2 namespace;
GREEN: 24 focused tests. Decimal overprecision RED demonstrated a value greater
than 5 silently rounding to 5; parser now rejects representation loss. DTO RED:
missing canonical DTO; nullable properties now remain serialized even with global
null omission. Taxonomy RED: two HTTP failures before endpoint; GREEN: two passes.
Independent SQL RED: missing reader before implementation. First provider run:
31 passed, one failed due to fixture missing required alt_text; fixture corrected.
Final focused verification: 33 passed, 0 failed, 0 skipped, including six real
SQL Server provider tests. Expanded Đà Nẵng/DA NANG coverage in the same provider
test subsequently passed. Release build: 0 errors; existing Pkcs dependency advisory
warnings remain unchanged. Full regression evidence is recorded in the PR after
the parent agent's coordinated run.

No connection strings, model weights or generated test artifacts are committed.

The isolated destination predicate explicitly uses query collation
`Latin1_General_100_CI_AI`: a real SQL Server probe and provider tests show
`Vietnamese_100_CI_AI` does not match Huế/hue or Đà Nẵng/da nang, while the chosen
collation matches both. No schema collation or legacy production predicate changes.
