# Staff complaint resolution — local validation

Approved scope and policies are recorded in `specs/staff-complaint-resolution-spec.md` and the matching plan. No published contract text was modified. Staff enters the actual contractual basis manually; signed-contract/itinerary embedding and automatic legal verification are not part of this implementation. Reconsideration is evidence intake/history only, without changing existing money or decisions.

## Verification

- API build succeeded (0 errors). Native API restarted successfully with the current code.
- Focused Application tests: 50 passed, covering the existing partial-refund negotiation and new Staff state, authorization, stale version, verified caps including disputed obligations, idempotence, rejection, evidence request expiry, retry after commit, and unchanged-money reconsideration.
- SQL Server migration test: 1 passed in an isolated database. Applied the migration twice; preserved old Declined complaints/evidence and checked status, foreign-key and operation uniqueness constraints.
- Focused new C# formatting completed with `dotnet format TripMate.slnx --no-restore --include ...`.
- Web: 52 complaint/operator tests passed and 24 Staff-screen/proxy/route tests passed. Focused ESLint and project-wide `npm run typecheck` passed.
- Local smoke checks: API health 200; FE administration login 200; Staff login 200; Staff queue BFF 200; Staff page 200. Queue is empty because historical Declined records were intentionally not reopened. No live monetary decision was issued to manufacture test data.

## Database safety

Full COPY_ONLY/COMPRESSION/CHECKSUM backup completed and RESTORE VERIFYONLY passed before the local migration. Copied backup: `D:/study/Project-Capstone/database-backups/TripMateDb-before-staff-review-20261127-053802.bak`. Applied `database/migrations/20261127_staff_complaint_resolution.sql` twice successfully to the existing local database. No resets, deletes or historical status backfills.

## Independent reviews

Fresh spec review and code-quality review completed. Fixed both blocking findings and obtained re-review confirmation:

1. A transaction retry could delete already committed evidence. Same-payload replay now succeeds; cleanup verifies persisted file references and preserves files when verification is unavailable. Added retry/duplicate/reconsideration regression coverage.
2. Downloads rejected valid original WebP and 6–10 MB evidence. Authenticated downloads now retain original UC68 compatibility; new supplements still require PDF/JPG/PNG, max 3 files at 5 MB each. Added download regressions. Keyed party panels by complaint ID to prevent stale actions across navigation.

## Limits

- No full solution test suite, full Web production build, remote CI, real payOS transfer, or visual browser acceptance pass was run in this memory-limited iteration. These focused results are not a claim of PR readiness.
- Evidence expiry uses the existing periodic complaint-deadline worker; its sweep interval remains the configured interval (hourly by default).
- If storage cleanup or its database verification fails, a private orphan may remain; committed evidence is preserved.
- No commit, push, merge, APK build, or mobile change was performed for this Web workflow.
