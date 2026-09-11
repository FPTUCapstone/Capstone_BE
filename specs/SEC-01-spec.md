# SEC-01 — Remove Insecure JWT Decode Fallback from Firebase ID Token Verification

Status: **IMPLEMENTED — awaiting team review / PR**

Branch: `feature/PhucTV-register-traveler` · Related: GitHub PR #2 review finding, UC-01 (Register Traveler), UC-04 (Sign In)

---

## 1. Overview

SEC-01 removes an insecure authentication fallback from `FirebaseAuthService`. Previously, when the Firebase Admin SDK could not be initialized (missing credentials), the service accepted Firebase ID tokens by **decoding** them locally with `JwtSecurityTokenHandler.ReadJwtToken()` — a decode-only operation that performs **no cryptographic signature verification**. Any attacker-crafted JWT with the right issuer string and a future `exp` claim was accepted as a verified Firebase identity.

The fix makes the **Firebase Admin SDK the only trusted verifier** of Firebase ID tokens. If the SDK is unavailable or rejects a token, authentication **fails closed**. Docker/local credential configuration was added so legitimate Firebase authentication continues to work.

## 2. Related Issue / Security Problem

- **Source:** PR #2 (`feat(auth): implement traveler registration flow`) review comment on `FirebaseAuthService.cs`.
- **Reviewer finding (translated):** the service used `ReadJwtToken()` as a fallback without verifying the Firebase signature. An attacker could self-sign a JWT with arbitrary email/UID claims, and the backend would treat it as a valid token that was never signed by Firebase.

## 3. Vulnerability Description

`JwtSecurityTokenHandler.ReadJwtToken()` is a **parser**, not a validator. It:

- does **not** check the JWT signature against any key,
- does **not** validate the audience (`aud`),
- provides no trust chain — any claim in the payload is attacker-controlled.

The old fallback activated whenever `FirebaseApp.DefaultInstance` was null (no Admin credentials configured) — which was the **default state of the repository's docker-compose setup**. In that state, a forged token such as:

```json
{ "iss": "https://securetoken.google.com/tripmate-82be3", "sub": "attacker-uid",
  "email": "<any known email>", "email_verified": "true", "exp": 9999999999 }
```

was accepted as a fully verified identity.

## 4. Root Cause

`FirebaseAuthService.VerifyIdTokenAsync()` contained a second code path, commented `// 2. Dev Fallback`, that ran when `_firebaseAuth == null`:

```
Firebase Admin unavailable
    ↓
JwtSecurityTokenHandler.CanReadToken(...)
    ↓
ReadJwtToken(...)                      ← decode only, no signature check
    ↓
issuer string check + exp check        ← both attacker-controlled, no security value
    ↓
FirebaseTokenValidationResult          ← indistinguishable from a verified token
```

The constructor also swallowed all initialization failures with only a warning, so a misconfigured deployment silently degraded into this insecure path in **every** environment, including Development and any production-like run without credentials.

## 5. Security Risk

With the fallback present and Admin credentials absent, an attacker could:

| Flow | Attack | Impact |
| --- | --- | --- |
| `POST /api/v1/auth/verify-email` | Forge token with `email_verified: true` + any known email | Account activated **and TripMate access + refresh tokens issued for the matched user** — account takeover (most severe) |
| `POST /api/v1/auth/google` | Forge token with any email | Auto-provision a new Active Traveler, or sign in as an existing user |
| `POST /api/v1/auth/register` | Forge token with matching `email` claim | Register any email without proving ownership (bypasses the Firebase token gate and email-match check) |

## 6. Scope

**In scope (implemented):**

- `src/TripMate.Infrastructure/Services/FirebaseAuthService.cs` — remove the decode fallback; fail closed when the Admin SDK is unavailable; correct misleading log messages.
- `tests/TripMate.Application.UnitTests/Services/FirebaseAuthServiceTests.cs` — new security regression tests.
- `docker-compose.yml`, `.env.example`, `README.md` — Firebase Admin credential configuration and setup documentation.
- `specs/UC-01-spec.md`, `plans/UC-01-plan.md` — remove stale documentation of the removed fallback.

**Explicitly out of scope:** see §7.

## 7. Out of Scope

1. **`IGoogleTokenValidator` / `GoogleTokenValidator`** — kept unchanged. It is a separate, existing validation mechanism (Google ID tokens / OAuth access tokens) with its own signature verification via `GoogleJsonWebSignature`. Its use as a secondary path in `GoogleAuthCommandHandler` is a separate concern.
2. **No `FirebaseUid` column** — no database/schema change; identity resolution remains by normalized email.
3. **No custom JWT/JWKS/RS256 validator** — the Admin SDK is the sole verifier; re-implementing verification locally was explicitly rejected.
4. **No handler/controller/error-code changes** — existing catch-and-map behavior in `RegisterTravelerCommandHandler`, `VerifyEmailCommandHandler`, `GoogleAuthCommandHandler` is reused as-is.
5. **No FE/Mobile changes**, no refresh-token/JWT-issuance changes (`JwtTokenService` keeps its legitimate `JwtSecurityTokenHandler.WriteToken` usage for *issuing* TripMate tokens), no OTP changes, no UC-04 feature work.

## 8. Approved Decisions

1. Completely remove/block `JwtSecurityTokenHandler.ReadJwtToken()` as an authentication fallback. If the Firebase Admin SDK cannot verify the token, authentication **must fail**.
2. The Firebase Admin SDK is the **only** trusted verifier for Firebase ID tokens.
3. Missing/unavailable Firebase Admin configuration must **fail closed** — in all environments, including Development.
4. Do **not** add `FirebaseUid` to the database.
5. Do **not** implement custom JWT/JWKS verification.
6. Keep `IGoogleTokenValidator` unchanged (separate mechanism, out of scope).
7. Preserve existing UC-01/UC-04 error behavior and contracts.
8. Configure Firebase Admin credentials for Docker/local development so legitimate Firebase authentication works.
9. Never commit Firebase service-account credentials or private keys.

## 9. Expected Authentication Flow

```
Request (Firebase ID token)
  |
  v
FirebaseAuthService.VerifyIdTokenAsync
  |
  v
Firebase Admin SDK VerifyIdTokenAsync        ← ONLY acceptance path
  |
  +---- valid ID token --------> FirebaseTokenValidationResult (trusted claims)
  |                              → existing account lookup / session flow
  |
  +---- invalid / expired -----> exception → existing handler error mapping
  |
  +---- Admin SDK unavailable -> InvalidOperationException (fail closed)
                                 → existing handler error mapping
```

There is **no** path such as:

```
Firebase Admin unavailable → ReadJwtToken() → accept decoded claims   ❌ REMOVED
```

## 10. Firebase Admin Credential Resolution

Resolution order in `FirebaseAuthService` (unchanged by this fix):

1. **Inline JSON:** `FIREBASE_SERVICE_ACCOUNT_KEY_JSON` (env/config) or `Firebase:ServiceAccountKeyJson`.
2. **File path:** `GOOGLE_APPLICATION_CREDENTIALS` (config key or environment variable; file must exist).
3. **Application Default Credentials (ADC).**

Docker wiring (Task 2): `docker-compose.yml` mounts `./secrets:/run/secrets:ro` and sets `GOOGLE_APPLICATION_CREDENTIALS=/run/secrets/tripmate-firebase-admin.json`. The developer places their own service-account JSON at `./secrets/tripmate-firebase-admin.json` (git-ignored via the existing `secrets/` rule). Setup instructions: `README.md` §9.1 and `.env.example`.

## 11. Failure / Error Handling

No new error codes, handlers, or response shapes. The existing per-handler `catch` blocks map the thrown exception:

| Flow | Handler | Failure mapping (unchanged) |
| --- | --- | --- |
| Register Traveler | `RegisterTravelerCommandHandler` | `AUTH_TOKEN_INVALID` |
| Verify Email | `VerifyEmailCommandHandler` | `MSG14` |
| Google Login | `GoogleAuthCommandHandler` | falls through to existing `IGoogleTokenValidator` chain; if that also fails → `MSG_GOOGLE_TOKEN_INVALID` |

Fail-closed guarantees:

- A token that is not verified by the Admin SDK **never** yields a `FirebaseTokenValidationResult`.
- No account lookup/creation, activation, or session issuance occurs after a verification failure.
- Startup without credentials logs a clear warning: *"Firebase Admin verification is unavailable; Firebase ID tokens will be rejected until credentials are configured."* The API still boots.

## 12. UC-01 Impact

- **Register Traveler:** token verification now succeeds only via the Admin SDK; the email-match check (`AUTH_EMAIL_MISMATCH`) and uniqueness rules operate only on genuinely verified emails. Failure → `AUTH_TOKEN_INVALID`.
- **Verify Email:** `email_verified` is trusted only from SDK-verified claims; failure → `MSG14`; no activation or session issuance on failure. This closes the account-takeover path described in §5.
- **Google Login (UC-01 provisioning):** Firebase-path verification is Admin-SDK-only; the existing Google-validator fallback is preserved unchanged.
- UC-01 spec/plan documentation no longer claims a local RS256/`JwtSecurityTokenHandler` fallback exists.

## 13. UC-04 Impact

UC-04 (Sign In) reuses `POST /api/v1/auth/google` and therefore the same verification chain. Effects:

- Firebase-path verification is now Admin-SDK-only; forged Firebase-style tokens are rejected.
- The Google-validator fallback and all documented UC-04 behavioral deltas (auto-provisioning rule, status matrix, role restrictions — see `specs/UC-04-spec.md` §9) are **unchanged** by SEC-01.
- Failure mapping for Google sign-in remains `MSG_GOOGLE_TOKEN_INVALID`.

## 14. Database Decision

**No database change.** No `FirebaseUid` column, no schema migration, no new tables. The `dbo.AuthProviders` table exists in schema v7 but is not used by this code and was deliberately not introduced. Identity resolution remains by normalized email.

## 15. Docker / Development Configuration

| Item | Value |
| --- | --- |
| Compose mount | `./secrets:/run/secrets:ro` (api service) |
| Environment variable | `GOOGLE_APPLICATION_CREDENTIALS=/run/secrets/tripmate-firebase-admin.json` |
| Developer-provided file | `./secrets/tripmate-firebase-admin.json` (own service-account JSON from Firebase console) |
| Git handling | `secrets/` is git-ignored (`.gitignore` rule); `.env` ignored; only `.env.example` tracked |
| Missing credential behavior | API boots; startup log states verification unavailable; all Firebase-token endpoints fail closed |

Local (non-Docker) options: `GOOGLE_APPLICATION_CREDENTIALS` environment variable, `dotnet user-secrets`, or inline JSON via `FIREBASE_SERVICE_ACCOUNT_KEY_JSON`. Full instructions: `README.md` §9.1.

## 16. Security Requirements

1. `ReadJwtToken()` / `JwtSecurityTokenHandler` must not appear in `FirebaseAuthService` in any form.
2. `FirebaseAuth.VerifyIdTokenAsync` (Admin SDK) is the only code path that can produce a `FirebaseTokenValidationResult`.
3. Unavailable Admin SDK ⇒ throw; never accept claims-only tokens; no environment-conditional acceptance path.
4. No custom signature verification; no trust-chain re-implementation.
5. Service-account JSON and private keys must never be committed, logged, or echoed.
6. Misconfiguration must be loud (clear startup logging), not silent degradation.

## 17. Acceptance Criteria

1. `FirebaseAuthService` contains no `JwtSecurityTokenHandler`/`ReadJwtToken` usage (verified by source-conformance test).
2. With the Admin SDK unavailable, `VerifyIdTokenAsync` throws for **any** token — including forged tokens with correct issuer and future `exp`.
3. Register / verify-email / Google login fail closed with existing error codes when verification is unavailable or fails.
4. With credentials configured, genuine Firebase ID tokens authenticate successfully (Admin SDK path).
5. All pre-existing tests pass unchanged; new security tests pass.
6. Docker Compose validates and documents credential wiring; `secrets/` path is git-ignored.
7. Documentation no longer describes the removed fallback.

## 18. Test Requirements

**Automated (implemented — `tests/TripMate.Application.UnitTests/Services/FirebaseAuthServiceTests.cs`):**

| # | Test | Proves |
| --- | --- | --- |
| 1 | `Verify_WhenFirebaseAdminNotConfigured_Throws_ForAnyToken` | Forged/unsigned token can never yield a trusted result when the SDK is unavailable |
| 2 | `Verify_WhenFirebaseAdminNotConfigured_NeverAcceptsExpiredOrForgedToken` | Invalid/forged tokens are rejected, not accepted |
| 3 | `Constructor_WhenNoCredentials_DoesNotThrow_ButLogsUnavailability` | Construction succeeds but explicitly logs the unavailable state |
| 4 | `Verify_WhenTokenEmpty_ThrowsArgument` | Existing empty-token contract preserved |
| 5 | `Source_FirebaseAuthService_ContainsNoJwtDecodeFallback` | Source contains neither `ReadJwtToken` nor `JwtSecurityTokenHandler` (regression guard) |

Current result: **58/58 passing** (53 baseline + 5 new). Tests use a plain forged-token string fixture — no JWT parsing logic is recreated in tests; the purpose is to prove the fallback is gone.

## 19. Manual Verification

**UC-01 Traveler Registration regression (passed after the fix):** valid registration; duplicate email; empty full name; empty email; invalid email; empty password; confirm-password mismatch; terms unchecked; invalid password; invalid phone; double submit; existing Firebase email; email whitespace; email uppercase.

**Security manual test (passed):** a forged JWT was supplied to the registration endpoint. Actual response — the forged token was **not accepted**:

```http
HTTP 400
{
  "success": false,
  "statusCode": 400,
  "message": "Invalid or expired Firebase authentication token: VerifyIdTokenAsync() expects an ID token, but was given a legacy custom token.",
  "data": null,
  "errors": { "code": "AUTH_TOKEN_INVALID" }
}
```

The expired-token manual case was intentionally skipped (covered by automated test #2 and Admin SDK semantics).

## 20. Definition of Done

- [x] Decode fallback removed; Admin SDK is the only acceptance path.
- [x] Fail-closed behavior implemented and unit-tested (5/5 new tests).
- [x] Full suite green: 58/58.
- [x] Docker/local credential configuration wired and documented (`docker-compose.yml`, `.env.example`, `README.md` §9.1).
- [x] UC-01 spec/plan documentation updated (no fallback claims).
- [x] No changes to `IGoogleTokenValidator`, handlers, controllers, error codes, schema, FE/Mobile.
- [x] No secrets committed; `secrets/` git-ignored.
- [x] Manual UC-01 regression + forged-token rejection verified.

---

## Acceptance Criteria / Checklist

- [x] `rg "ReadJwtToken|JwtSecurityTokenHandler" src/TripMate.Infrastructure/Services/FirebaseAuthService.cs` → no matches.
- [x] Whole `src/` tree: only legitimate `JwtTokenService.cs` `WriteToken` issuance remains.
- [x] `dotnet build TripMate.slnx` → 0 errors (after the separate POI build fix).
- [x] `dotnet test` → 58/58.
- [x] `docker compose config -q` → pass; `git check-ignore secrets/tripmate-firebase-admin.json` → ignored.
- [x] Forged JWT rejected by the live API (HTTP 400 `AUTH_TOKEN_INVALID`).
- [x] No environment (including Development) retains a decode-based acceptance path.

## Risks

| Risk | Mitigation |
| --- | --- |
| Local dev without credentials breaks Firebase-token flows | Intentional fail-closed (decision #3); mitigated by README §9.1 / `.env.example` setup docs and actionable startup logging |
| `FirebaseApp.DefaultInstance` is process-global/static | No code path in the repo creates it during tests; noted in the test file for future maintainers |
| Developers accustomed to credential-free runs see new auth failures | Troubleshooting row added to README §10 pointing to §9.1 |
| Secret mishandling | `secrets/` git-ignored; read-only compose mount; documented "never commit" rules |

## Notes for Reviewer

1. Review `FirebaseAuthService.cs` first: the entire diff is the fallback removal + fail-closed throw + 3 log-message corrections. Confirm no acceptance path remains when `_firebaseAuth == null`.
2. The 5 new tests are the security contract; test #5 is a source-conformance guard (reads the production source and asserts the fallback identifiers are absent).
3. `IGoogleTokenValidator` is intentionally untouched — verify `GoogleTokenValidator.cs` / `GoogleAuthCommandHandler.cs` have zero diff.
4. The branch also contains **separate** changesets that must not be conflated with SEC-01: baseline merge-artifact fixes (`IApplicationDbContext.cs`, `TestDbContext.cs`) and the POI build fix (`ApiControllerBase.cs`). See `plans/SEC-01-plan.md` §14 for commit separation.
