# UC-65 Confirm Payout Settlement Backend Plan

1. **Done:** Introduce an Administrator-only settlement list query at `GET /api/v1/admin/payout-settlements`, with server-fixed `ApprovedForSettlement` scope and Staff denied. A settlement detail query remains part of the command-screen work.
2. Implement a transactional authorization command that changes status to `SettlementProcessing` and persists an outbox/work record.
3. Implement the payout-processing worker with gateway adapter, idempotency, bounded transient retry, and terminal outcome persistence.
4. Add authorization, transition, atomicity, worker-outcome, and retry-bound tests.
5. Keep manual retry and operational reconciliation as future approved use cases.
