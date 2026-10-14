# PR #54 — TourCategory Phase 1.1 review handoff

## Authority and scope

Report 3 SRS V2 is authoritative. The repository's [TM-70 specification](../specs/TM-70-spec.md) and [plan](../plans/TM-70-plan.md) reference the frozen Docs decision at `2ad6916dc1ff4f314c19e13258c088533a93cd9a`, V2 D6 / addendum section 8. This review does not introduce a business decision or imply new Product/BA approval.

| Phase | Boundary |
|---|---|
| 1.1, this PR | Schema/domain/EF foundation only; category remains nullable; no new endpoints |
| 1.2, deferred | Approved taxonomy seeds, backfill and final mandatory-category enforcement; Product/BA seed approval pending |
| 1.3, deferred | Rating feasibility and index review |
| 2, deferred | Search/filter expansion and taxonomy read contract |

No production seed, backfill, mandatory-category rule or assignment/writer API is added. Domain setters remain private. UC-34/35 assignment behavior requires a separate approved contract. Unfiltered searches retain legacy NULL-category Tours; future explicit active-category filters match only assigned Tours and must not be disabled globally.

## Database changes and affected files

`catalog.TourCategories` has identity `category_id`, stable case-insensitive `code`, English `name` and `is_active` defaulting to 1. Named trusted CHECK constraints reject blank code/name. A unique code index and active-name index support the taxonomy. `commerce.Tours.category_id` is a nullable FK with a category index. No many-to-many membership is added.

Affected files: the category migration and v7 fresh schema; `Tour`, `TourCategory`; `ApplicationDbContext`, both EF configurations; category domain/mapping/migration tests, search schema-parity tests and the API integration test project; TM-70 spec/plan and this handoff record.

## Metadata compatibility audit

The migration checks parent/column ownership, trusted/enabled CHECK constraints, canonical nonblank predicates, and the BIT default value 1. CHECK comparison removes presentation whitespace, brackets and parentheses; DEFAULT comparison removes presentation whitespace and parentheses. It intentionally accepts only the canonical expression family, not arbitrary supposedly equivalent SQL. Unknown definitions fail closed with shape mismatch. No comparison is relaxed without a concrete target-version failure.

Index validation checks expected keys/order, ascending direction, nonclustered type, uniqueness, filters and enabled/non-hypothetical state. Unsafe same-name objects are rejected before committing. All migration changes remain in one XACT_ABORT transaction with rollback in CATCH; rollback tests protect prior schema/data. Future SQL versions or alternative equivalent expressions require verification before adoption, not automatic acceptance.

Target verification uses SQL Server 2022 (`mcr.microsoft.com/mssql/server:2022-latest`). Existing 16 category cases cover fresh schema, baseline upgrade, rerun, extra parentheses, incorrect CHECK/DEFAULT/PK/FK definitions, untrusted FK and invalid index shapes. The target compatibility test also exercises levels 140, 150 and 160 on SQL Server 2022, rerunning the migration while checking canonical default/constraint behavior. Test-only fixture rows are not production seeds.

## Develop synchronization and reviewer focus

Previous reviewer-fix commits `03d27efcd68230d56ebf829996f2187642eb73bd` and `ba0b56f673818ec7d9ddde4b25e2343167baa695` added traceability, defensive metadata checks and regression coverage. This pass normally merges develop `1b0cc9f39e64f55f6a0f33550a0389cee6c9d7c0`; no rebase/force push. The merge resolves automatically in the fresh schema, DbContext and test project. Category additions remain additive alongside newer trip-review/navigation and commercial-service migrations. Develop-owned seed files brought by synchronization are not category seeds and are not part of this PR's diff against develop.

Review the PR diff against develop, fresh/upgrade parity and rollback assertions, and SQL Server 2022 CI with no SQL/Redis skips. Require final-head Release build and complete regression evidence in the PR description. `READY_FOR_RE_REVIEW` is a technical handoff, not independent approval or merge authorization.

## P2 code-encoding risk and Phase 1.2 decision gate

Finding: `TOUR_CATEGORY_CODE_ENCODING_DECISION_PENDING`. Severity: P2.
Disposition: `NON_BLOCKING_PHASE_1_1`. Blocking scope:
`PHASE_1_2_SEEDS_AND_WRITERS`.

Confirmed behavior:

- `TourCategory.Create` trims `code`, rejects null/blank values and values longer
  than 50 .NET characters. It has no character-class, normalization, casing or
  database-code-page validation, so arbitrary Unicode input is currently accepted.
- EF maps `Code` with maximum length 50, `.IsUnicode(false)`,
  `Latin1_General_100_CI_AS`, required storage and a unique index.
- Both the migration and fresh schema use
  `VARCHAR(50) COLLATE Latin1_General_100_CI_AS NOT NULL`; the unique index is
  `UX_TourCategories_Code`.
- A value outside the column code page may be converted lossily during SQL Server
  persistence, creating a potential stable-code identity or collision risk. No
  concrete collision is claimed or evidenced by this review.

This remains non-blocking for Phase 1.1 because the PR only introduces the
schema/domain/EF foundation, adds no production taxonomy rows or category writer,
keeps existing Tour category IDs nullable, and does not expose a path that creates
production category codes. It is a hard gate before Phase 1.2 seeds or writers.

### Product/BA decision register

| Required decision | Status |
|---|---|
| ASCII-style machine identifier or Unicode identifier? | PENDING |
| Allowed character classes? | PENDING |
| Normalization and casing rules? | PENDING |
| Maximum length and uniqueness semantics? | PENDING |
| Is the current case-insensitive collation acceptable? | PENDING |
| Retain `VARCHAR` or migrate to `NVARCHAR`? | PENDING |
| Rejection behavior for invalid identifiers? | PENDING |
| Migration and backward-compatibility requirements? | PENDING |
| Approval of proposed production taxonomy codes and names? | PENDING |

The unresolved implementation directions are: (A) approve a restricted code
format and add matching Domain/SQL validation, or (B) approve `NVARCHAR` storage
with a reviewed migration and contract adjustments. Neither direction is approved.
No functional implementation or tests asserting a speculative policy are part of
this documentation remediation.

Current classification: `P2_DOCUMENTED_NON_BLOCKING`;
`PHASE_1_2_PRODUCT_BA_DECISION_PENDING`; `PR54_FORMAL_REVIEW_PENDING`.
