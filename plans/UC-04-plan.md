# UC-04 Implementation Plan — Sign In

> Latest work: Revision 3, T16-T23, at the end of this file. Earlier tasks/results are historical snapshots. Final status (2026-09-15): W01/T16 contract gate DONE; T17-T20 implemented and locally verified; T21 core acceptance completed; T22/S01 Web refresh/session restoration completed; T23 Sign Out/revoke-on-signout intentionally deferred to the separate Sign Out UC. UC-04 SIGN IN: DONE — see "Final delivery status" at the end of this file.

## Revision 2.1 plan — approved 2026-09-14

Reference: [UC-04 spec revision 2.1](../specs/UC-04-spec.md).
The existing T1–T10 below describe implemented v2.0 work, not pending tasks.
This revision explicitly extends their BE-only Scope Guard to the verification guard and
the narrowly listed FE removal/error-mapping changes. User approval authorizes implementation.

### T11 — Setup and regression baseline

- Work on existing `feature/PhucTV-sign-in` (BE) and `feature/PhucTV-sign-in-web` (FE),
  as explicitly requested by the user; preserve unrelated work. No new fix branch delivery.
- Baseline already recorded: 232 BE tests pass, 8 SQL Server checks skipped.
- Re-read team engineering/review prerequisites before implementation tasks.

### T12 — Google Administrator role gate (RED → GREEN → review)

- Files: GoogleAuthCommandHandler.cs, AuthErrorCodes.cs, ApiControllerBase.cs;
  GoogleAuthCommandHandlerTests.cs and GoogleSignInIntegrationTests.cs.
- RED: replace Administrator success test with rejection; add normal and concurrency
  re-fetch cases asserting no session persistence/user mutation and normal no-token generation.
- GREEN: shared account gate retaining status precedence, then rejecting Administrator;
  call in normal lookup and re-fetch. Add constant and HTTP 403 mapping.
- Verify: focused Google handler/API tests; confirm Traveler/TourOperator and auto-provision
  success plus existing status/claim failures. Review guard placement before mutations.

### T13 — Close verify-email Google session path (RED → GREEN → review)

- Files: VerifyEmailCommandHandler.cs, its unit tests and auth API integration tests.
- RED: Administrator + Google provider rejected before activation/mutation/session issuance;
  cover statuses and retain Locked/Inactive precedence. Assert non-Google verification and
  Administrator email/password sign-in still work.
- GREEN: provider/role gate before mutations/session issuance with the same 403/code.
- Verify: focused verification/login/API tests; review all Firebase session issuance paths.

### T14 — Narrow Web alignment and reference updates

- FE files: SignInForm.tsx, authErrorMapper.ts and relevant tests.
- Audit existing admin rendering first: Google is currently hidden for admin; preserve this
  and add regression coverage rather than inventing a button removal if none exists.
- Map new public Google rejection; never display raw SDK/server internals.
- Update contradictory BE BR-18 comments/tests/references; retain v2.0 decision history.
- Do not implement the broader FE redesign or replace the existing admin password simulation.

### T15 — Verification and final review

- Focused tests after each change; full `dotnet test TripMate.slnx` after implementation.
- Applicable FE tests, lint and typecheck; report environment limitations and skipped checks.
- Review final diff twice: revised spec compliance, then authorization/mutation/regression risk.
- Definition of done: Administrator cannot obtain a new session through either audited Google
  token path; password and non-admin flows retain behavior; available required checks pass.
- Delivery correction: commit/push/PR ownership belongs to the developer; delivery was intentionally deferred until final validation and explicit approval. Work remains on existing sign-in branches.
  Broader FE sign-in spec completion still waits for all checklist approvals.

### Execution evidence — 2026-09-14

- T11–T15 implemented on existing sign-in branches as requested; PR creation belongs to user.
- RED: auth unit suite had 7 failing cases, and new HTTP integration theory had 3 failing
  cases under v2.0, demonstrating Google sessions were issued to Administrator.
- GREEN: normal Google lookup and concurrency re-fetch share a status-first account gate;
  verify-email checks Administrator/Google before activation or token issuance.
- Full BE suite: **255 passed, 0 failed, 8 skipped** (baseline 232 passed/8 skipped).
- FE baseline: 54 tests passed. Final FE: **57 tests passed**; lint, typecheck and production
  build passed. Existing unrelated FE edits remain outside the task commit.
- Whitespace formatter applied to changed C# files; diff whitespace check passed.
- Self-review, spec axis: revised BR-18, status precedence, no mutation/session rejection,
  password regression and non-admin Google regression covered; no blocking findings.
- Self-review, standards axis: rules stay in Application; controller only maps failures;
  no schema, dependency, secret or unrelated product-flow changes; no blocking findings.
- Real SQL Server concurrency remains unverified because the test connection is not configured;
  deterministic InMemory concurrency re-fetch covers Administrator rejection.
- Mobile has no dedicated mapping for the new code; BE still rejects the request, but Mobile
  may show a generic auth failure. Mobile UI work remains outside the approved scope.
- Existing JWT sessions are not revoked. Administrators without usable passwords require
  recovery/provisioning. The existing Web admin password form remains a prototype.

---

| Item | Value |
|---|---|
| Spec | [`UC-04-spec.md`](../specs/UC-04-spec.md) **v2.1** (user-approved 2026-09-14) |
| Status | **Implemented T1–T10 (2026-09-13) + review fixes P1 (BR-02 race), P2a (LastLoginAtUtc), P2b (structured logging) — pending PR review** |
| Branch | `feature/PhucTV-sign-in` (merged `origin/develop` — CI/CD + registration snapshot; conflicts resolved in favor of UC-04 v2.0) |
| Workflow | **AI prerequisite: before EVERY task — re-read `Dev_and_CrossReview_Checklist.pdf` + `TEAM_ENGINEERING_RULES.docx` and cross-check the task against both.** TDD, one atomic task at a time, red → green → review → next; NO commit/push without explicit user request |
| Prerequisite | ✅ Done: UC-01 BR-06 amendment + `Suspended` cleanup (same day) |

---

## Scope Guard

This plan touches ONLY the files listed in §T (below) plus their tests.
**Never touch:** database schema, `dbo.Messages`, `dbo.AuthProviders`, any non-auth endpoint,
FE/Mobile code. Business failures return `Result`/`Result<T>`; no generic repositories; no EF
migrations.

---

## Task T1 — Login: block `PendingApproval` (spec BR-05, code change #7)

- **RED** (`LoginCommandHandlerTests`): account `PendingApproval` with valid credentials →
  `Result.Failure(auth.account_pending_approval)`; assert zero mutation (no refresh row, no
  `LastLoginAtUtc`).
- **GREEN**: add constant `AccountPendingApproval` to `AuthErrorCodes`; add switch case
  `AccountStatus.PendingApproval` → 403 mapping input; update the class doc comment
  (it currently claims PendingApproval must NOT block).
- **Files**: `AuthErrorCodes.cs`, `LoginCommandHandler.cs` (+ doc comment), tests.

## Task T2 — Firebase claims surface (spec BR-11/BR-12 enabler, code change #10a)

- **RED** (`FirebaseAuthServiceTests`): verifying a sample Firebase ID token exposes
  `SignInProvider` (from nested `firebase.sign_in_provider`) and `EmailVerified` (top-level,
  absent ⇒ reported as not-verified, never defaulted true).
- **GREEN**: extend the verify result record + mapping in `FirebaseAuthService` (Admin SDK
  claims dictionary); add typed `FirebaseUnavailableException` thrown when the SDK is not
  initialized / infrastructure fails (distinct from a verification rejection).
- **Files**: `IFirebaseAuthService` / `FirebaseAuthService.cs`, new exception type, tests.

## Task T3 — Google: remove fallback + BR-11 + BR-12 (spec §3.2-4..8, code changes #1/#2/#3/#10b)

- **RED** (`GoogleAuthCommandHandlerTests`):
  - Firebase verify fails → `AUTH_TOKEN_INVALID` **and no fallback** to `GoogleTokenValidator`
    (assert the validator is never invoked);
  - provider claim ≠ `google.com` / missing → `AUTH_TOKEN_INVALID`;
  - `email_verified` false **and MISSING** → `MSG_EMAIL_NOT_VERIFIED`, zero mutation;
  - token without email → `AUTH_TOKEN_INVALID`.
- **GREEN**: delete the raw-Google fallback branch; add the two checks between verify and email
  extraction; route infrastructure failures to `FirebaseUnavailableException`.
- **Files**: `GoogleAuthCommandHandler.cs`, `GoogleTokenValidator` removed from DI usage (keep
  class deletion decision to review — prefer deleting registration + class), tests.

## Task T4 — Google: remove auto-activation + block `PendingApproval` (spec BR-03/BR-05/BR-14, code changes #4/#5)

- **RED**: `PendingEmailVerification` existing account → `403 MSG_UNVERIFIED` and **Status
  unchanged** (flips the old expectation — deliberate, PR-note required); `PendingApproval`
  existing account → `403 auth.account_pending_approval`, zero mutation.
- **GREEN**: delete the auto-activate block; add the PendingApproval case.
- **Files**: `GoogleAuthCommandHandler.cs`, tests.
- **PR-note** (mandatory): *"Deliberate contract alignment: Google Sign In no longer
  auto-activates PendingEmailVerification; the account remains unchanged and receives
  MSG_UNVERIFIED."*

## Task T5 — Google response G1-A (spec BR-17, code change #6)

- **RED**: response carries `role`, `email`, `fullName`, `accessTokenExpiresAtUtc` alongside the
  preserved `userId/status/accessToken/refreshToken/isNewAccount`; values sourced per contract
  (role/status from DB; expiry = now + configured lifetime); enum serialized as string.
- **GREEN**: extend `GoogleAuthResponse` record + handler mapping; verify Swagger stays additive.
- **Files**: `GoogleAuthResponse.cs`, `GoogleAuthCommandHandler.cs`, tests.

## Task T6 — Session persistence transaction (spec BR-15, code change #11)

- **RED**: success path persists refresh row + `LastLoginAtUtc` inside
  `ExecuteInTransactionAsync` (assert single atomic commit; simulate post-persist failure →
  nothing persisted); failure path persists nothing.
- **GREEN**: wrap session persistence in `ExecuteInTransactionAsync` for both handlers; token
  generation + response after commit.
- **Files**: `LoginCommandHandler.cs`, `GoogleAuthCommandHandler.cs`, tests.

## Task T7 — Controller contract: body-only + ProblemDetails 400 (spec §6.3/§7.3, code change #8)

- **RED** (integration): `POST /auth/google` without body token → **400 ProblemDetails with
  `errorCode = AUTH_TOKEN_MISSING`** (currently an ApiResponse envelope — must change); request
  with only a Bearer header and empty body → 400 (header no longer an input).
- **GREEN**: remove the `ExtractBearerToken()` fallback; build the 400 via
  `Problem(...)` with `errorCode` extension.
- **Files**: `AuthController.cs`.

## Task T8 — `HandleFailure` mapping audit (spec §7.4, code change #9)

- **RED**: `auth.account_pending_approval` maps to 403; `auth.firebase_unavailable` maps to 503;
  every google errorCode maps to 401/403 (none swallowed by the `_ => 400` default).
- **GREEN**: extend the switch; verify `MSG_GOOGLE_TOKEN_INVALID`/`MSG_EMAIL_NOT_VERIFIED` map
  correctly (fix if needed).
- **Files**: `ApiControllerBase.cs`.

## Task T9 — Integration suite + OpenAPI gate (spec §11-D)

- Integration (WebApplicationFactory): login 200 shape (8 fields, string enums); 3×401
  identical **security-relevant** fields (dynamic fields excluded); google 200 G1-A shape
  (9 fields); 503 Firebase-unavailable (mocked `FirebaseUnavailableException`); status gates
  per matrix; failure-theory asserts (`LastLoginAtUtc` + refresh rows unchanged on every
  failure path).
- OpenAPI assertions: google request body-only `idToken`; G1-A fields present; string enums;
  `errorCode` extension; NO Bearer parameter; no out-of-scope endpoints.
- **Files**: `TripMate.Api.IntegrationTests/Authentication/*`.

## Task T10 — Verification (Definition of Done for this plan)

- `dotnet build` → 0 errors, 0 warnings;
- `dotnet test` → all suites green (Application ~123+new, API 30+new, Infrastructure 3);
- Swagger diff reviewed → additive only;
- Traceability walk: every BR-01…BR-18 (minus retired BR-13) has its guard green (spec §14);
- Self-review vs `Dev_and_CrossReview_Checklist.pdf` §II–III; then hand to user for review —
  commit/push/PR **only on explicit user request**.

---

## Execution Order & Dependencies

```
T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8 → T9 → T10
```
(Each task: red tests first, then green, then self-review before moving on. T7/T8 may run in
parallel with T5/T6 after T4 — sequential is fine for this size.)

## Deliberate Changes Register (for the PR description)

1. Google Sign In no longer auto-activates `PendingEmailVerification` (T4).
2. `PendingApproval` now blocks sign-in on both methods (T1/T4).
3. Bearer header is no longer an input channel for `/auth/google` (T7).
4. Google 400 missing-token error shape changed from ApiResponse envelope to ProblemDetails (T7).
5. Google response gains `role/email/fullName/accessTokenExpiresAtUtc` (T5).
6. Firebase infra failures now return 503 `auth.firebase_unavailable` (T2/T3/T8).
7. Global JSON: `JsonStringEnumConverter` registered — runtime now emits enum values as strings
   (e.g. `"Traveler"`), matching the enum-as-string OpenAPI schema the API already documents
   (discovered drift: login/Google responses previously emitted numeric enums).

## Out of Scope (must not appear in the PR)

DB schema changes · `dbo.Messages` seeds · `dbo.AuthProviders` · FE/Mobile code · rate limiting ·
refresh/logout endpoints · AGENTS.md edits (separate docs pass).

## Revision 3 plan - narrowed core and approved legacy resolver

Status (updated 2026-09-15): W01/T16 documentation/audit DONE and APPROVED; T17-T20 implemented and locally verified; T21 core acceptance DONE; T22/S01 Web refresh/session restoration DONE; T23 Sign Out/revocation deferred to the separate Sign Out UC. UC-04 SIGN IN: DONE (see "Final delivery status" at the end of this file). [BE spec](../specs/UC-04-spec.md), [Web plan](../../Capstone_FE/plans/UC-04-web-plan.md). T1-T15 and the W01/T16 gate records plus the T17-T20 execution-evidence sections below are historical snapshots. No commit/push/PR performed; delivery deferred pending explicit approval.

| Task | Dependency | Files/areas | Verification / DoD |
| --- | --- | --- | --- |
| T16 BE side of joint W01 documentation/evidence gate - DONE | Joint read-only audit/documentation DONE; user sign-off recorded | User.cs EmailVerifiedAtUtc, VerifyEmailCommandHandler.cs, GoogleAuthCommandHandler.cs timestamp writers, IJwtTokenService; raw status checks incl CreatePoiCommandHandler.cs and Admin handlers | Establish trusted persisted verification provenance; no response fallback/profile/status inference; inventory relevant account gates versus role/application gates; exact new routes/errors reviewed |
| T17 shared resolver - IMPLEMENTED / LOCALLY VERIFIED | T16 | proposed Authentication/Common/AccountEligibilityResolver.cs (location review), LoginCommandHandler.cs, GoogleAuthCommandHandler.cs, relevant eligibility consumers/tests | Resolver BEFORE old legacy gate, including re-fetch; two matching pairs PASS; conflict/missing profile/nonoperator/uncertain verification FAIL; effective Active response without DB status mutation |
| T18 current auth context - IMPLEMENTED / LOCALLY VERIFIED | T17 | AuthResponseDto.cs, GoogleAuthResponse.cs, proposed current profile/context reader, JWT generation mapping/tests | Effective account status distinct from DB; current applicationStatus; Active missing profile session retained; consistent relevant API eligibility, no accidental business grant |
| T19 Web sync-only - IMPLEMENTED / LOCALLY VERIFIED | T16 | proposed Web verification CQRS/DTO, AuthController.cs, validators/error mapping/tests | Zero credentials/session/cookie; fresh evidence; restrictions and operational errors; existing Mobile response/admin guard unchanged |
| T20 Web login/Admin/cookie - IMPLEMENTED / LOCALLY VERIFIED | T17-T19 | explicit Web contracts/controller, proposed cookie service, handler/API tests | Admin gate before persistence; failed B preserves A; access/context+HttpOnly cookie; persistence fixed original7days |
| T21 core acceptance - DONE (2026-09-15) | T17-T20, FE core | handler/API/persistence/real Web tests | Resolver matrix, no account/profile mutation, baseline regression/Mobile compatibility; runtime dependencies were later resolved by the FE core (W02-W06) plus S01/CR-11; final rules/formatter/build/test audit PASS |

### Separate session work package
T22 refresh/current-state restoration - DONE (2026-09-15): POST /api/v1/auth/web/refresh redeems the HttpOnly tripmate_refresh cookie through the same shared resolver (SHA-256 hash + revoked_at/expires_at validation, current account/role/profile, fresh short-lived access token; no rotation, no new session, no sliding expiry; raw refreshToken never in Web JSON). FE restoration: WebSessionProvider mounted in the root layout drives a single-flight cold-start restore; useWebSession exposes restoring/authenticated/unauthenticated; CR-11 guards consume the settled authoritative context. Broader session-package hardening (bounded eligible-401 API retry, manual retry joining the same single-flight, transient handling) remains deferred.
T23 sign-out cleanup dependency - OUT OF UC-04 SCOPE (deferred 2026-09-15): normal Sign Out / revoke-on-signout belongs to the separate Sign Out UC (D02). No revoke endpoint or cookie-deletion flow was added; the owning Web sign-out session contract (revoked_at + matching cookie expiry, idempotency, JWT15minute limitation) stays with that UC.

### Deferred / owning UC
Previous T18 approval lifecycle and T23 normalization tasks are replaced by this revision, NOT scheduled core implementation. Keep old Approve handler/DB statuses intact. Actual normalization and new registration/approve/reject/resubmit writers require future coordinated release/data audit. Multi-tab/business modules deferred. Inventory account checks (e.g. CreatePoi uses Active but also role-specific gates) must resolve genuine eligibility conflicts only, not grant unrelated role/business permissions.

Evidence rule: accepted MVP marker is valid persisted Users.EmailVerifiedAtUtc, with DB authoritative; existing verify writes only on PendingEmailVerification transition. Handler response EmailVerifiedAtUtc fallback does not prove persisted verification. Missing reliable evidence - unresolved; alternative provider/evidence/backfill needs reviewed contract, no new implicit Firebase dependency to ordinary password/refresh/API requests.

Before implementation reread both team docs/handoff; final contract/spec/plan review then meaningful RED/GREEN/spec+standards review. Focused tests then full dotnet test/build/format and applicable FE/Mobile compatibility checks. CORE/session/dependency results separate; no migration execution, commit/push/new branch. No mock or skipped dependency counted PASS.

### W01 / T16 - Documentation-only contract gate (DONE; historical gate record)

W01 is the FIRST FE-BE agreement step, not a feature implementation. Source files authApi.ts, authErrorMapper.ts, controllers and DTOs are inspection/reference areas only in W01; do NOT modify runtime code/tests, create sessions or change DB. W02 and BE implementation tasks begin only after user approval of the matching final contract.

Approved contract checklist:

- Exact POST routes: /api/v1/auth/web/login, /api/v1/auth/web/admin/login, /api/v1/auth/web/google, /api/v1/auth/web/verify-email. Existing Mobile routes remain unchanged.
- Password/Admin request: application/json with email:string, password:string, keepMeSignedIn:boolean (default false). Example: {"email":"user@example.com","password":"...","keepMeSignedIn":false}. Email trim/lowercase rule; password untouched. Google JSON: idToken:string and keepMeSignedIn:boolean, body-only Firebase token. Verification: Firebase Bearer, no body. Final required/null/unknown-field and validation rules documented before code.
- Sign-in success envelope data types: userId:number (BE integer ID, NOT string); email:string; fullName:string (non-null, empty allowed); role:string enum; status:string effective authentication status; applicationStatus supported string enum for TourOperator (nonoperator always null, field required); accessToken:string; accessTokenExpiresAtUtc:UTC ISO8601 string. Google adds isNewAccount:boolean. Final field nullability and complete success example must agree in both specs.
- Web response has NO refreshToken. Refresh credential only in HttpOnly cookie; internal token issuance may reuse existing BE services without changing the Mobile response.
- Cookie NAME = tripmate_refresh, FINAL/APPROVED; never invent a different name during code. Attributes: HttpOnly, Secure production, SameSite=Lax with HTTPS same-site deployment, Domain omitted, Path=/api/v1/auth. OFF: session cookie; ON: persistent until original seven-day expiry; no rotation/sliding expiry. Local development exceptions and cookie deletion scope documented.
- Legacy resolver BEFORE old legacy gate: DB PendingApproval/profile PendingApproval or DB Rejected/profile Rejected with required role/profile/verification evidence -> effective response status Active, applicationStatus from profile, NO DB account/profile-status update. Conflicts/insufficient evidence -> no session, auth.account_state_unresolved; HTTP403 APPROVED.
- Trusted verification evidence source/provenance established by BE audit. Accepted MVP: DB authoritative; valid persisted EmailVerifiedAtUtc is sufficient evidence; no profile/status inference or response fallback. Keep ordinary password/refresh independent of new implicit Firebase calls.
- Complete endpoint-specific error table: HTTP status + stable code + exact FE display message + action (field error, resend, retry, support, wrong Admin entry). Include validation, credentials, provider/token, account restrictions, Admin Google/Admin role, legacy unresolved, verification and operational/unknown failures. Codes, not message text matching. W01 is NOT DONE while message mappings/catalog remain placeholders.
- Preserve existing Mobile token response/verification session/admin Google guard. Identify current Web Register consumers as compatibility dependency; no implicit client/header-based dispatch or global envelope rewrite.
- Explicit success/error parser contracts: sign-in/Google success envelope, failures ProblemDetails/errorCode; verification follows the APPROVED W01 packet format; Mobile retains existing format. Agree casing/string enums, no role/approval guessing.
- Refresh/sign-out exact schemas are separate session/dependency contract work; do not require full implementation of those UCs in W01. Any field/cookie decision needed by sign-in or cleanup integration must nevertheless be fixed before its consumer code.

Definition of Done: both FE/BE specs contain identical APPROVED route/request/DTO/error/message/cookie/effective-status/evidence decisions; user sign-off recorded 2026-09-14. No code implementation evidence required. User approved the core contract, cookie, evidence, HTTP403 unresolved error and complete error/message/action catalog. W01/T16 = DONE (documentation/audit only); W02/T17+ implementation remains NOT EXECUTED.


## W01/T16 approved solution - DONE (historical gate record)

W01 (FE docs) + T16 (BE docs/read-only audit) complete ONE contract gate together; user approval recorded; W02/T17+ prerequisites satisfied (runtime tasks not executed). No T16 runtime implementation prerequisite.

Both specs contain the identical W01/T16 review packet: audited timestamp writers/provenance limitations, APPROVED persisted verification-marker rule, FINAL cookie tripmate_refresh, full HTTP/code/exact-message/action and SDK/fallback catalog. Follow the APPROVED packet; do not invent choices in W02.

Gate status: W01/T16 = DONE, user-approved core contract and code evidence audit. DB authoritative valid timestamps suffice for MVP; suspicious live-data audit is release/migration work. W02/T17+ and other runtime tasks NOT EXECUTED.

DoD includes byte-identical packet in BE/FE specs, no placeholder catalog/cookie, clear source-data trust assumption and compatibility guard; final user approval recorded before implementation. Changes solve earlier vague candidate, unnamed cookie and incomplete mappings without new schema/provider dependency or owning-UC implementation.
## Approved W01/T16 clarification - applicationStatus and evidence

applicationStatus is REQUIRED in EVERY successful Web sign-in and refresh authentication-context response, type string | null; never omitted. Traveler and Administrator -> null. TourOperator with recognized profile -> Approved/PendingApproval/Rejected exactly; missing/unresolved/unsupported profile -> null. BE must serialize this field even if global JSON configuration normally omits nulls. FE validates key presence and type; null for a valid TourOperator triggers application-unresolved UI, not auth cleanup. A missing field is a contract violation, never treated as approved; retain the previously approved valid-session application-unresolved handling rather than signing out solely for application data. Unknown/malformed application status from a nonconforming response remains fail-closed for business access. Verification-only responses are NOT authentication context and do not add applicationStatus. Existing Mobile contract is not silently changed.

Accepted MVP evidence: DB is authoritative; persisted Users.EmailVerifiedAtUtc with CreatedAtUtc <= timestamp <= BE current UTC is sufficient evidence for legacy resolver. NULL/implausible timestamp -> account_state_unresolved. No additional per-record provenance proof or whole-production-DB audit required to implement the resolver. Audit suspicious/imported/manual legacy data before migration/release of those records; do not turn that operational audit into an implementation gate. No data repair/backfill/migration in resolver.

Plan verification: assert applicationStatus key present for Traveler/Administrator (null), each recognized operator status, unresolved operator (null), sign-in/refresh. Assert valid stored evidence passes, NULL/future/pre-creation fails with no session/status mutation. Add serialization coverage to prevent null omission. W01/T16 evidence mechanism and applicationStatus shape are APPROVED by user; Cookie/error-copy decisions are now APPROVED too; this documentation update does not authorize or execute runtime implementation.


## User sign-off record - 2026-09-14

APPROVED: core Web endpoints/request/DTO contract; cookie tripmate_refresh; HTTP403 auth.account_state_unresolved; current error/message/action catalog including Web auth.request_invalid; Web verify-only success ApiResponse and defined failures ProblemDetails/errorCode; fullName non-null string, empty allowed; applicationStatus always present string | null; DB-authoritative valid verification timestamp evidence. W01/T16 = DONE (docs/audit contract gate only). No implementation/test/migration execution by this update. Separate refresh/sign-out schema and deferred owning-UC tasks are not declared DONE. No commit/push/new branch.


## T17 execution evidence - 2026-09-14 (historical snapshot)

- User authorized next implementation step, with no commit/push/PR/new branch. Work remains on feature/PhucTV-sign-in; pre-existing documentation changes preserved.
- Preparation: re-read TEAM_ENGINEERING_RULES.docx, Dev_and_CrossReview_Checklist.pdf, BE AGENTS.md, handoff and implementation/TDD guidance. Approved seams: password/Google handlers, persistence effects and HTTP failure mapping.
- Baseline: 255 passed, 0 failed, 8 SQL Server checks skipped.
- RED: both matching legacy password cases failed (pending rejected; rejected response remained DB Rejected). Both matching Google cases also failed before its integration.
- GREEN: shared AccountEligibilityResolver checks explicit account restrictions before legacy compatibility. Exact matching profile states and persisted CreatedAtUtc <= EmailVerifiedAtUtc <= BE now permit effective Active. Missing/conflicting profile, nonoperator role, absent/implausible evidence and unsupported account state fail with auth.account_state_unresolved, mapped to HTTP403.
- Password, existing Google and Google concurrency re-fetch use the resolver before session/user mutations. No Firebase call introduced for password, no account/profile/evidence normalization. Existing token response fields remain available to Mobile; effective account status follows the approved compatibility rule. Mobile verify-email session behavior remains unchanged.
- Eligibility inventory: CreatePoi requires current Administrator AND stored Active; no eligible legacy TourOperator can reach that capability, so no conflict needing a business-UC edit. Admin approval's raw status check belongs to the deliberately preserved lifecycle; detail display is not an eligibility gate. JWT has identity/role claims, no account-status claim. Refresh will reuse resolver in T22.
- Coverage: matching pairs; role/profile conflicts; missing/unsupported profile; null/future/pre-creation timestamps; stored-evidence requirement despite verified Firebase claims; normal/race failure mutation checks; HTTP403/errorCode and persisted rejected state on HTTP success. Existing Active TourOperator with no profile still authenticates.
- Final full BE suite: 274 passed, 0 failed, 8 skipped (216 Application, 55 API, 3 Infrastructure). Build: 0 warnings, 0 errors. Scoped dotnet format whitespace completed.
- Two-pass self-review: spec compatibility/order/mutation boundaries, then standards/layers/query scope/security; no blocking findings. One profile query only for otherwise eligible legacy accounts. Real SQL Server concurrency remains unverified because the configured SQL test dependency is absent; deterministic re-fetch tests passed.
- T17 is locally verified, pending human cross-review/delivery. T18 full current authentication context/applicationStatus, T19-T23 and FE implementation are not declared complete. Historical NOT EXECUTED entries above describe the earlier documentation gate.
- No commit, push, staging, PR, migration or production DB mutation performed.


## T18 execution evidence - 2026-09-14 (historical snapshot)

- Scope: current account/application context and Web DTO projection only; no new Web routes/cookies yet (T20), no refresh endpoint (T22), no FE/Mobile code, commit/push/branch/migration.
- Preparation: both team rules documents re-read before changes; implementation follows approved handler/serialization seams and RED/GREEN workflow.
- RED: seven password current-context cases failed because ApplicationStatus was absent; dedicated Web DTO contract also absent. Final tests assert actual handler values and JSON behavior instead of reflection/type-existence checks.
- Shared resolver now returns CurrentAccountContext containing effective Status and current ApplicationStatus. Active TourOperator reads current persisted profile; exact supported strings only; missing/unsupported profile -> null with valid session. Nonoperators -> null. Legacy resolver uses the same profile read for eligibility and context, without a second lookup or DB normalization.
- Password and existing Google handlers propagate context; Google concurrency re-fetch replaces context from the winning account. Unknown-email Google still provisions Traveler/Active with null application state.
- AuthResponseDto/GoogleAuthResponse carry application state internally with JsonIgnore to preserve Mobile JSON. New WebAuthResponseDto/WebGoogleAuthResponseDto expose applicationStatus explicitly, exclude refreshToken, preserve non-null fullName (empty allowed), integer userId, current role/effective status and access-token expiry. Web Google adds isNewAccount. JsonIgnoreCondition.Never keeps applicationStatus present even with global WhenWritingNull.
- Coverage: Traveler/Admin null; each supported operator value; missing/invalid profile valid session; legacy context propagation and race path; profile change reloaded on subsequent sign-in; Web password/Google JSON null presence/no refresh credential; unchanged Mobile credential fields/no application field. Existing HTTP regression and Mobile verify-email tests remain green.
- Verification: full BE suite 292 passed, 0 failed, 8 SQL Server checks skipped (234 Application +55 API +3 Infrastructure); scoped whitespace formatter completed. Build: 0 warnings, 0 errors.
- Two-pass self-review: approved context/evidence/session separation and Mobile compatibility, then Application-layer boundaries/read-only profile queries/no new dependencies or secrets. No blocking findings. No account/application state added to JWT claims; identity/role claims remain unchanged and application approval must use current backend state.
- T18 locally verified; human cross-review/delivery still pending. Web HTTP integration of these DTOs awaits T20 and refresh response awaits T22; neither is counted implemented by DTO serialization tests. Earlier NOT EXECUTED text above is historical gate state. Next task T19 Web verification-only.


## T19 execution evidence - 2026-09-14 (historical snapshot)

- Preparation: re-read TEAM_ENGINEERING_RULES.docx and Dev_and_CrossReview_Checklist.pdf; follow approved T19 HTTP/CQRS contract and TDD. User delivery prohibition remains: no commit/push/PR/new branch/migration.
- RED: HTTP test returned 404 for POST /api/v1/auth/web/verify-email before implementation. GREEN: exact endpoint now takes Firebase Authorization Bearer (no credential body), dispatches WebVerifyEmailCommand and returns ApiResponse data containing only emailVerified:true.
- Dedicated Web handler has no IJwtTokenService dependency and no RefreshTokens write. It validates Firebase evidence, verified flag and usable normalized email, finds account, blocks Locked/Inactive/unsupported status, then synchronizes the existing PendingEmailVerification transition only. Active/legacy statuses are not normalized or repaired; subsequent sign-in applies eligibility/application gates. Idempotent repeat does not replace verification timestamp.
- Existing Mobile verify-email handler/route/header parser and Administrator Google session guard remain unchanged. A compatibility HTTP test calls Web sync then Mobile verification: zero refresh rows after Web; existing Mobile access/refresh fields and exactly one session afterwards.
- Approved Web failures: missing/malformed Bearer ->401 AUTH_HEADER_MISSING; invalid/expired token ->401 MSG14; unverified ->403 MSG_EMAIL_NOT_VERIFIED; missing evidence email ->400 auth.verification_email_missing; account missing ->400 MSG_USER_NOT_FOUND; Firebase unavailable ->503 auth.verification_unavailable; account restrictions ->403 established codes. Defined failures are ProblemDetails/errorCode. SDK internals not returned; caller cancellation propagates.
- 17 new real HTTP pipeline tests use a controlled Firebase service (not live Firebase): success exact one-field data/no Set-Cookie/no session/LastLogin; strict header rejection without SDK call; evidence/operational failures without account mutation; restriction failures preserving existing refresh record; repeat/idempotency and legacy status preservation; Mobile compatibility. Existing auth/registration/Google tests remain green.
- Full command: dotnet test TripMate.slnx --no-restore --logger trx --results-directory TestResults/UC04-T19. Result: 309 passed, 0 failed, 8 skipped (234 Application, 72 API, 3 Infrastructure). Build: dotnet build TripMate.slnx --no-restore ->0 warnings/0 errors. Scoped dotnet format whitespace passed.
- Raw local evidence: [test-output.txt](../TestResults/UC04-T19/test-output.txt), [build-output.txt](../TestResults/UC04-T19/build-output.txt), individual TRX reports and [summary](../TestResults/UC04-T19/evidence.md). TestResults is already gitignored; logs are local review artifacts, not staged.
- Two-pass self-review: approved Web sync/session separation/error contract and compatibility, then standards/layer/read-only checks/query/mutation scope. No blocking findings. No verification-only routing context or application approval fallback introduced; no changes to approval lifecycle or schema.
- Limitations: live Firebase verification and real SQL Server behavior not exercised by these tests; 8 existing SQL Server checks skipped. FE recovery/link integration and new Web sign-in/Admin/cookie session are still pending, not declared end-to-end complete. T19 locally verified, human cross-review/delivery pending; next T20.


## T20 execution evidence - 2026-09-14 (historical snapshot)

- User authorized continuation, without commit/push/PR/new branch/migration. Both team rules documents re-read before implementation; approved Web contract and existing handlers inspected.
- RED: four Web login/Admin HTTP tests returned 404 before endpoints existed. GREEN: POST /api/v1/auth/web/login, /web/admin/login and /web/google now return approved Web DTOs with applicationStatus always present and no refreshToken.
- Dedicated WebPasswordSignInCommand normalizes email, preserves password and returns approved field codes (errors.email/password arrays of MSG01/MSG02) through Result/ProblemDetails; binding/type/null-boolean failures get Web-only auth.request_invalid. Unsupported Content-Type ->415. Existing Mobile binding/error behavior remains unchanged.
- Admin entry is server-selected by endpoint and passed as an internal LoginCommand gate; client DTO has no role/gate authority. Shared credentials/current eligibility checks execute first; non-Administrator fails auth.admin_access_required/403 BEFORE refresh generation/persistence, LastLogin or JWT issuance. Public password and Mobile contracts retain normal login behavior. Google retains existing Administrator rejection and current-context/re-fetch paths.
- Refresh expiry is exposed internally (JsonIgnore) from session issuance and used for cookie persistence; one password timestamp fixes created/expiry exactly seven days apart. Cookie tripmate_refresh: HttpOnly, SameSite=Lax, Path=/api/v1/auth, no Domain. Production and HTTPS use Secure; only Development HTTP localhost may omit Secure. KeepMeSignedIn false omits Expires; true uses original refresh expiry, not another computed lifetime. Token stored hashed in DB, excluded from Web JSON. Failed replacement emits no Set-Cookie and leaves prior refresh record intact.
- CORS uses explicitly configured origins and now enables credentials; exact allowed/disallowed preflights tested. Production deployment still requires approved HTTPS same-site topology and configured FE origin; this task does not deploy or add Origin/custom-header hardening.
- 29 new HTTP tests: password cookie/expiry/hash/access15min, Administrator success, nonadmin gate, client-role/gate spoof rejection, failed replacement preservation, binding/field/credential/content-type failures, account-before-role precedence, legacy/application contexts including unresolved null, existing/new Google and Google Admin denial, environment Secure and CORS.
- Full regression first found 2 Development test-fixture DI failures (scoped hasher from root); fixed fixture to create/dispose scopes. Failed-run log/TRX retained; final run is explicitly identified in evidence.md.
- Final command: dotnet test TripMate.slnx --no-restore --logger trx --results-directory TestResults/UC04-T20. Final result: 338 passed, 0 failed, 8 skipped (234 Application +101 API +3 Infrastructure). Build: 0 warnings, 0 errors. Scoped dotnet format whitespace and git diff --check passed.
- Evidence: [final test output](../TestResults/UC04-T20/test-output.txt), [build output](../TestResults/UC04-T20/build-output.txt), [TRX links/history](../TestResults/UC04-T20/evidence.md). Local TestResults artifacts already gitignored; no staging performed.
- Two-pass self-review: approved Admin-before-session/DTO/cookie/error/account rules, then standards/Application business gates/controller orchestration/query/secret/regression scope. No blocking findings. Existing registration, Mobile verification and auth regression suites pass.
- Limits: HTTP tests run in WebApplicationFactory with InMemory DB and controlled Firebase evidence; browser session restoration, live Firebase and real SQL Server are unverified. Eight SQL checks remain skipped. Refresh/sign-out implementation stays T22/T23; do not claim rotation/revocation/session-restoration acceptance from sign-in tests. FE core not yet implemented; T21 requires FE core and current dependency results. Next FE work starts W02; consolidated T21 acceptance remains pending.


## Final delivery status - 2026-09-15

This section is the current authoritative status and supersedes the "pending"/"NOT EXECUTED" wording inside the historical gate and execution-evidence sections above (which are preserved unedited as historical snapshots, including their test counts).

- W03 Password Web Sign-in: DONE.
- W04 Google Web Sign-in: DONE.
- W05 Email verification recovery: DONE.
- W06 Partner routing: DONE.
- T21 core acceptance: DONE — resolver matrix, no account/profile mutation, baseline regression and Mobile compatibility verified with the completed FE core.
- T22 / S01 Web refresh + session restoration: DONE — POST /api/v1/auth/web/refresh redeems the HttpOnly tripmate_refresh cookie (SHA-256 hash match, revoked_at/expires_at rejection, current user/role/profile via the shared AccountEligibilityResolver, fresh short-lived access token; non-rotating, no sliding expiry, no Set-Cookie, raw refreshToken never in Web JSON). FE: WebSessionProvider mounted in the root layout drives a single-flight cold-start restore; useWebSession exposes restoring/authenticated/unauthenticated; persisted tripmate_user metadata never authenticates. Manual browser F5 verification passed.
- CR-11 direct-route authorization: DONE — shared partnerRouteDecision policy + PartnerRouteGuard wrap /partner/register, /partner, /partner/application and /partner/application/resubmit (resubmit is its own Rejected-only permission), evaluated only after S01 settles. Client guards are navigation protection; future protected Partner action endpoints must still enforce server-side authorization.
- T23 normal Sign Out / revoke-on-signout: OUT OF UC-04 SCOPE — intentionally deferred to the separate Sign Out UC (D02). No revoke endpoint or cookie-deletion flow was added.
- UC-02 registration backend and real resubmit backend: separate UCs; not implemented here.
- UC-04 SIGN IN: DONE.
- Final rules/formatter/build/test audit: PASS — dotnet format --verify-no-changes exit 0; Release build 0 warnings/0 errors; full BE suite 372 passed / 0 failed / 0 skipped with the SQL Server test connection configured (364 passed / 8 environment-gated skips when the connection is unset); FE 237 passed / 0 failed with lint/typecheck/build exit 0; git diff --check clean on both repos.
- Delivery: commit/push/PR ownership belongs to the developer; delivery was intentionally deferred until final validation and explicit approval. No staging, commit, push, PR or branch change performed.
