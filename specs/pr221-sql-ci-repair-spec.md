# PR 221 SQL regression repair

Scope: repair four observed SQL CI failures without weakening the payment-channel gate, ownership, concurrency or refund obligations.

- Tour authoring/submission fixtures require a genuinely Ready operator payment channel; credentials and beneficiary values are synthetic. Disabled channels must still allow drafts and block review submission without writes.
- Refund detail continues to prefer active obligations, then completed refunds, then terminal Failed/Rejected attempts. A failed historical attempt must not hide the completed refund or its original gateway. Existing priority for processing/pending obligations remains intact.
- Do not change payment, refund or booking state as part of the read operation. No database migration, production configuration or real provider operation is needed.

Acceptance: reproduce the failures; pass the failing SQL tests and a Disabled-channel negative case; cover failed-before/after-completed legacy refunds and existing active-obligation priority; pass the changed-file C# formatting check; push the fix to the existing PR branch.
