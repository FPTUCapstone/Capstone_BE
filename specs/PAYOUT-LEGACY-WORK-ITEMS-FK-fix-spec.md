# Payout legacy work-items FK migration fix

## Scope

Fix `20261108_remove_legacy_payout_request_tables.sql` so it can remove the
superseded `payment.PayoutSettlementWorkItems` table before dropping
`payment.Payouts`.

The table exists in the shared Azure SQL database, references
`payment.Payouts(payout_id)`, and has no reader or writer in the current
codebase.

## Acceptance criteria

- The migration drops `payment.PayoutSettlementWorkItems` before
  `payment.Payouts`.
- `payment.PayoutAdjustments` remains present, but its legacy foreign key to
  `payment.Payouts` is removed.
- Current daily-settlement tables remain present.
- Running the migration a second time is a safe no-op.
- No application, API, refund, or payout calculation behavior changes.

## Out of scope

- Reconciling `dbo.SchemaMigrations` in Azure SQL.
- Running or deploying the migration against Azure SQL.
- Changing the daily payout business rules.
