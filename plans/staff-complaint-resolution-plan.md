# Staff complaint resolution plan

1. Inspect final-decline/domain/store/constraints, staff authorization and refund creator. Tests first for final decline to review and no premature refund.
2. Add durable Staff review events/state/schema, idempotent thin API handlers. Tests: Staff-only, stale version, replay/payload mismatch, caps, rejection, final decision, atomic rollback and no refund duplication.
3. Add private evidence-request/response/download with party ownership. Evidence request expires after 72 hours to Staff review (no automatic rejection); accept reason/new-evidence reconsideration requests separately without decision/refund changes. Preserve existing negotiation and completed refund states.
4. Integrate FE Staff list/detail/decision UI plus party statuses/evidence actions. Never direct an unresolved amount complaint to the separate Admin transfer-dispute queue.
5. Focused tests/format/lint/typecheck and fresh spec/quality reviews; verified local backup + isolated SQL migration/repeat test before live local apply; restart apps and read-only API checks. No real payment or destructive reset.
6. Completed-refund correction: derive read-only closure from the related completed refund, gate GET actions and evidence/reconsideration writes (including a transaction recheck), expose isClosed, hide party forms and retain history/downloads. Regression coverage for Traveler/TO, unrelated source references and legacy Processed refunds. No migration or existing financial-state update.
