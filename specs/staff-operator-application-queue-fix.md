# Staff operator application queue repair

Authorized scope: restore real registration applications in the existing Staff queue. Existing detail/approve/reject behavior is preserved. Administrator does not acquire Staff permission.

GET /api/v1/admin/tour-operator-applications is Staff-only, private/no-store. It returns {items,totalCount}, 50 items per page, newest submission first with user ID as tie-breaker. Optional page, status (All/Pending/Approved/Rejected), search (company/tax code/exact user ID, at most 200 characters). No passwords, document storage references, or signing tokens are returned. Existing profile/document tables are read without migration or data changes.

FE calls its authenticated same-origin BFF, displays loading/error/retry separately from an actual empty result, and applies search/status/pagination at the server. Existing explicitly supplied list fixtures remain supported.

Plan: (1) reproduce missing network loading with failing FE tests and BE query test; (2) add bounded query and Staff-authorized collection route; (3) wire BFF and queue; (4) validate query, role boundaries, FE behavior, types/lint and build BE; (5) restart local BE and verify persisted rows via authenticated HTTP. Review spec compliance and quality before completion.
