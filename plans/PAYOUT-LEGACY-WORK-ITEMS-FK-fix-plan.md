# Payout legacy work-items FK migration fix plan

1. Add a SQL Server integration test that reproduces the Azure schema drift:
   `PayoutSettlementWorkItems.payout_id` references `Payouts.payout_id`.
   Verify the current migration fails.
2. Add the minimal guarded `DROP TABLE` for the superseded work-items table,
   before dropping `payment.Payouts`.
3. Re-run the focused test and the existing payout migration regression suite.
4. Review the diff for scope, idempotency, and data-safety boundaries.

## Definition of done

- Focused SQL Server test passes with zero failures and zero skips.
- Existing payout migration tests pass.
- `git diff --check` passes.
- No commit, push, PR, deployment, or Azure SQL mutation is performed.
