# Checklist v2.2 (condensed)

Source: `Dev_and_CrossReview_Checklist v2.2.pdf` (workspace root). Use with `TEAM_ENGINEERING_RULES v2.2.docx`. Cite codes in findings (e.g. `C13`). Results: Đ pass-with-evidence / F fail / C unchecked / NA. Empty is not pass.

## Gates
G0 scope/AC clear · G1 right branch/base, baseline inventoried · G2 contract/code/SQL/UI same semantics, tests for happy/error/boundary/race · G3 self-review + lint/format/test/build/security with evidence on right head · G4 independent reviewer, required checks, conversations resolved · G5 merge rights, dependency/config/migration order, handoff

## Scope & workspace
- C01 Jira/UC, actor, Web/Mobile scope, in/out-of-scope, AC with source/version
- C02 SRS/Docs/SQL/runtime/Figma reconciled; discrepancies have a decision record (do not pick a side silently)
- C03 endpoints/screens/tables/dependency PRs/unmerged parts listed; fixture/mock not called real integration
- C04 small spec+plan with file/test/expected result; approvals recorded
- C05 branch `<type>/<member>-<description>`; Web/Mobile feature PR targets `develop`
- C06 head/base, git status, worktree list recorded; staged/unstaged/untracked and pubspec.lock preserved
- C07 baseline has command/SDK/exit code/pass-fail-skip; pre-existing failures separated from regressions
- C08 AC -> code/API/DB/UI -> I/D/U/V case -> evidence mapped

## API, auth, architecture
- C09 route/method/status/body/header match runtime, Docs, OpenAPI, client; Idempotency-Key in OpenAPI if required
- C10 required/optional/null/empty, camelCase, enum/bounds/timezone, pagination defined and tested
- C11 controller only binds/auths/dispatches/maps; Domain independent of EF/HTTP; no forced generic repository
- C12 Web/Mobile use feature layers + existing API client/DI; no raw request in widget; no unneeded framework/global store
- C13 real auth path tested: missing/wrong/expired token, wrong role, other owner; identity from session not body
- C14 guest browse = public contract; protected actions need auth; prototype credentials are not auth evidence
- C15 400/401/403/404/409/5xx, malformed/non-JSON, timeout, network error mapped; upstream errors never become success
- C16 no secret/stack/internals/PII in response/log; cookie/token, origin/CSRF, CORS checked

## SQL, schema, migration, query
- C17 Domain/EF/SQL consistent: type, length, nullable, enum, default, CHECK, FK, UNIQUE
- C18 full schema and migration reach the same canonical schema (database-first, no EF migrations); see C63
- C19 migration tested on empty DB, old DB with data, rerun; preflight, impact/rollback; no silent data deletion
- C20 parent/child save via navigation, no FK = 0; round-trip with new context (D02)
- C21 datetime2/DateTimeOffset converters, UTC/date-local contract, precision and boundaries
- C22 transaction boundary; no orphans on child failure; key uniqueness/race (I02, D01)
- C23 filter/count/projection/sort/page in SQL; stable tie-breaker; no unbounded load, N+1, Cartesian explosion
- C24 provider-dependent diffs have real SQL tests; InMemory/SQLite/skipped tests do not prove SQL Server behaviour

## Environment
- C25 worktree/head/process/port is the right feature
- C26 real SDKs available (dotnet/node/flutter); device ID and flags correct
- C27 Docker/compose healthy; host port vs container network
- C28 origin reachable from the client (Android emulator 10.0.2.2, real phone host)
- C29 Next production build clean with required Firebase config; `npm run dev` is not `npm run build`
- C30 CORS allowlist/preflight; map SDK key/quota/HTTPS when map in scope
- C31 test data owner/cleanup; no Figma/API keys in prompts; leaked secrets revoked

## Build, test, CI, self-review
- C32 BE: restore + Release build whole solution incl. tests + test + formatter; check duplicate code after merge
- C33 Web: npm ci, lint, typecheck, test if any, build. Mobile: pub get, format, analyze, tests
- C34 each run records head/base SHA, env, command, exit code, pass/fail/skip, artifact
- C35 critical SQL tests not skipped; "286 pass, 17 skip" is not 303 pass; rollback/race tests really create the situation
- C36 CI really ran green on the right commit; CodeRabbit skipped/unselected workflow is not test evidence
- C37 `git diff --check`; scoped format; no debug/unused code; no full-repo normalize or generated files
- C38 self-review source/docs/PR body consistent; vulnerability scan result or stated limit

## Cross review & findings
- C39 confirm repo/PR URL; record reviewed head/base; find earlier conversations
- C40 read full diff vs AC; separate regression / baseline / scope conflict / verification gap
- C41 happy/error/boundary/race simulated where relevant; provider limits recorded
- C42 finding states condition, actual/expected, impact, source rule, file/line on right head, repro, acceptance test
- C43 severity by impact: P0 urgent; P1/P2 block per defect/AC; P3 usually non-blocking; suggestion optional
- C44 do not force wrappers/generic repo or unsourced business rules
- C45 merge same-cause findings; list all known findings in one round; adjust verdict when asked
- C46 verdict explicit (Request changes/Approve) + unverified parts; never "fully bug-free" from green tests

## Fix, merge, cleanup
- C47 each finding: root cause, fix commit, regression test/result, docs
- C48 re-review delta since last reviewed head; new checks on the right ref
- C49 PR description current: test counts, migration/config/dependency, limitations
- C50 no blocking Request Changes; valid approval; reviewer != author
- C51 unresolved outdated/collapsed threads checked; MERGEABLE is not enough if BLOCKED
- C52 FE/Mobile feature target `develop`; no bypass of protected rules; merge/config/migration order clear
- C53 before deleting a worktree: inventory, backup, verify recoverability
- C54 handoff: worktree/head/PR/commands, prototype/unverified parts, dependencies

## Cross-repo contract & final-head gate
- C55 nullability/optional map consistent across DB, DTO/OpenAPI, Web, Mobile; tests with null/empty/missing
- C56 real (sanitized) data regression for parser/UI, not only pretty fixtures
- C57 shared catalogue/category IDs from authoritative API/config; no divergent hardcoded sets in prod
- C58 UI no-op states (same keyword, active filter, same URL, back/forward) must not stick loading or hide empty/error
- C59 known limitation needs disposition before merge: fix, or follow-up with owner+ticket+approval, or block; never counts as AC pass
- C60 deploy env contract (server/public/mobile config) verified on preview/prod with smoke request
- C61 review automation evidence: config parses, job actually ran on final head; skipped/fallback/green bot is not independent review
- C62 after sync/conflict/remediation: re-review delta, required checks, BE -> Web/Mobile contract smoke on final head
- C63 fresh-vs-upgrade schema parity with one inventory/normalizer; diff must be empty or have decision record + owner + impact + cleanup

## Simulation cases (run only when relevant)
- I01 lost response after commit, retry same key -> same ID, one aggregate
- I02 concurrent duplicate requests -> one commit, no unexpected 500
- I03 old key + new content -> new op client-side; server rejects key with different fingerprint
- I04 same payload different keys / same key different users -> respect key scope
- I05 double click, success, retry after validation failure, dispose/reopen, restart
- D01 child write fails after parent insert -> full rollback incl. operation record
- D02 FK != 0 and timestamps correct when re-read with new context
- D03 invitation get-or-create / regenerate boundaries (expiresAt == now, usedCount == maxUses)
- D04 unique collision vs connection loss vs other DB errors; bounded retry only if designed
- D05 DB upgrade parity: full-schema DB vs migrated baseline DB, rerun, legacy/wrong shape
- U01 HTTP 200 but wrong data/UI (missing field, wrong type, HTML instead of JSON)
- U02 form errors and access (blank/0/null/boundary, bad token/role, other user's ID, 404)
- U03 stale search response must not overwrite newer one; filter/page/back/refresh
- U04 empty catalogue/photos, 404 images; no fake IDs/counts/ratings/images
- U05 timeout/conflict on submit: never fake success; 409 needs the right confirm action
- V01 responsive 320..1440 / text scale; no overflow or hidden CTA
- V02 keyboard and Flutter lifecycle; no setState after dispose
- V03 Figma frames vs contract; demo content removed; frame -> component -> field table
- V04 real map: pan/zoom/pin/fit/GPS denied/key error, list fallback
- V05 public entry, deep links, guards, back keeps state
