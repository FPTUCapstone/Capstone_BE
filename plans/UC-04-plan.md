# UC-04 Implementation Plan — Sign In (Spec v2.0)

| Item | Value |
|---|---|
| Spec | [`UC-04-spec.md`](../specs/UC-04-spec.md) **v2.0** (ratified + user-approved 2026-09-13) |
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
