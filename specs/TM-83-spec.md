# TM-83 / UC-37 Submit Tour for Approval

Authorized scope: complete the existing Backend, Web and Mobile submission flow, independently review/fix it, and test actual UI interaction. Source: Report 3 section 3.8.2.3, exported 2026-11-18 from https://docs.google.com/document/d/1VwPkqRyC6ZlbXBQC0ztdektt0vAyE3qt/edit. Jira TM-83 explicitly assigns review to Staff; stale Administrator wording does not change the existing Staff moderation boundary.

- Only an active approved Tour Operator may submit an owned Draft or Rejected tour. Pending/Approved/Published cannot be submitted or directly edited.
- Server readiness covers mandatory tour data, itinerary activity/day, destination, active cover image, cancellation-policy snapshot and a valid priced future departure. The detail checklist and command share the evaluator. Invalid requests leave status, metadata and audit unchanged.
- Confirmation identifies the tour and explains Pending, read-only and no public booking/discovery until Staff approval/publication. Cancel/Escape/backdrop do not mutate data. Prevent duplicate clicks and successful duplicate audits; concurrent requests must not produce HTTP 500.
- Persist an optional trimmed submission note (empty becomes null) and an independent UTC submission timestamp. Technical input/storage limit: 4,000 UTF-16 code units, matching existing Tour.TermsMaxLength; this is not presented as a new SRS business rule.
- Existing POST /api/v1/operator/tours/{tourId}/submit-review accepts optional JSON {"submissionNote":string|null}; no-body clients remain compatible. Success retains tourId/status=Pending and adds submittedAtUtc. Operator detail and Staff review detail expose submissionNote/submittedAtUtc.
- Canonical SQL schema, explicit idempotent migration, EF UTC datetime2 mapping and real SQL tests must agree. Do not fabricate legacy submission timestamps.
- Reuse existing pages, layered architecture, shared components and tokens. Handle stale async results, 401, permissions, missing resources, validation, state conflicts and ambiguous network outcomes safely. No redesign of approval, booking or payment workflows.

Verification: focused baseline and regression tests first; meaningful boundary, endpoint, SQL migration/concurrency and widget tests; static checks/build; independent review; real browser/Android interaction using isolated fixtures, checking messages, ordering, keyboard/focus, small-screen/zoom/overflow and retry/cancel. Report unexecuted checks honestly. No commit, push, PR or merge has been requested for this task.
