# TO monthly commission implementation plan

Approved scope: monthly fixed commission statements, TripMate platform payOS webhook, owned TO payment screens, carry-forward of held revenue and late completed refunds. Keep historical statements immutable and retain legacy payout routes.

1. [x] Inspect existing contracts and define colocated specification and test cases.
2. [x] Implement closed Vietnam month eligibility, per-source ledger, original-rate refund credits, deterministic round-half-up allocation, held rows and excess-credit carry. Serialize issuance per TO and enforce unique sources in SQL.
3. [x] Implement owned/admin read APIs, fixed-amount durable payment attempts, reuse/recovery, controlled Mock completion and platform webhook dispatch distinct from booking/channel payments.
4. [x] Connect TO sidebar/dashboard/list/detail and admin list/detail; show calculation credits and held rows, zero payable without payment action, dd/MM/yyyy dates and right-aligned amounts.
5. [x] Review, run focused checks, verify backup, apply additive migrations and start correct local BE/FE.

## Verification (27/11/2026)

- Application commission/read/payment and related Staff complaint tests: 68 passed, no skips.
- PayOS gateway tests including safe stale-attempt recovery: 14 passed.
- Isolated SQL Server migrations: 2 passed. Repeatable application, parent data retained, source deduplication, active-attempt uniqueness and conserved credit balance verified.
- Web commission tests: 8 passed; focused ESLint and npm run typecheck passed.
- Relevant C# formatting completed; API/integration build succeeded without warnings/errors. Removed redundant inherited test-context properties to avoid new hiding warnings.
- Independent compliance/quality reviews completed; currency filtering, credit-label clarity, zero payable and checkout expiry findings addressed.
- Verified COPY_ONLY backup: database-backups/TripMateDb_before_commission_20261127_182307.bak (workspace root).
- Applied 20261127_commission_payment_tracking.sql and 20261127_commission_monthly_lines.sql to local TripMateDb. No statements, payment attempts or ledger rows were generated during verification.
- Native BE localhost:5022 health 200. Correct FE localhost:3001; admin login, commission list and admin screen returned 200. Existing local statement count is zero.

## Operational boundaries

- Local payment provider remains Mock; no real payOS payment was created or charged during verification.
- Admin issues a closed month at /admin/commission-statements. TO uses /partner/commission-statements.
- Historical statements without source attribution are preserved; ambiguous old cohorts are held and explicitly reported for review rather than automatically billed again.
- No commit, push, PR or production deployment performed.
