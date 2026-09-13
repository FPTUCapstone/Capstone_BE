# SEC-01 Implementation Plan

Status: **IMPLEMENTED — awaiting team review / PR**

Branch: `feature/PhucTV-register-traveler` · Spec: [`specs/SEC-01-spec.md`](../specs/SEC-01-spec.md)

> This plan documents what was implemented for SEC-01 (remove the insecure `ReadJwtToken()` fallback from Firebase ID-token verification), why each file changed, and how the change is verified. It is written for reviewers and team members reproducing or auditing the work.

---

## 1. Objective

Remove the `JwtSecurityTokenHandler.ReadJwtToken()` fallback from `FirebaseAuthService` because it decodes JWT claims without cryptographic verification. The Firebase Admin SDK must be the only trusted verifier of Firebase ID tokens; when it is unavailable or rejects a token, authentication must fail closed. Additionally, configure Firebase Admin credentials for Docker/local development so legitimate Firebase authentication continues to work, without ever committing secrets.

## 2. Current Problem

Before SEC-01, `FirebaseAuthService.VerifyIdTokenAsync()` had two paths:

1. **Primary:** Firebase Admin SDK `VerifyIdTokenAsync` (secure) — used only when credentials initialized successfully.
2. **Fallback (insecure):** when `_firebaseAuth == null`, the service decoded the token locally (`CanReadToken` → `ReadJwtToken`), checked only the issuer string and `exp`, and returned the attacker-controlled claims as a trusted `FirebaseTokenValidationResult`.

Because the repository's docker-compose setup shipped **no** Firebase Admin credentials, the fallback was the *active* verification path in the standard local run — and it was reachable in every environment, including Development. A forged JWT with the correct issuer string and a future `exp` was accepted as a verified identity, enabling account takeover via `/api/v1/auth/verify-email` and `/api/v1/auth/google`, and ownership-free registration via `/api/v1/auth/register`.

## 3. Implementation Strategy

Executed as three atomic tasks under strict TDD (Red → Green → Refactor), each verified before proceeding:

| Task | Content | Verification |
| --- | --- | --- |
| **Task 1 — Security core** | RED: write `FirebaseAuthServiceTests` (3 security tests failed against the vulnerable code, proving the tests exercise the real fallback). GREEN: remove the fallback, add the fail-closed throw, fix 3 misleading log messages. REFACTOR: none needed. | `dotnet test --filter FirebaseAuthServiceTests` (5/5), full suite (58/58), source search (no `ReadJwtToken`/`JwtSecurityTokenHandler` in the service) |
| **Task 2 — Docker/dev credentials** | `docker-compose.yml`: read-only `./secrets:/run/secrets` mount + `GOOGLE_APPLICATION_CREDENTIALS` env var. `.env.example`: document the credential requirement and how to provide it. `README.md`: §9.1 setup section + troubleshooting row. | `docker compose config -q`, `git check-ignore`, full suite (58/58) |
| **Task 3 — Documentation truthfulness** | `specs/UC-01-spec.md` and `plans/UC-01-plan.md`: remove the two stale claims that a local RS256/`JwtSecurityTokenHandler` fallback exists. | Terminology search (no stale claims), `git diff --check`, full suite (58/58) |

## 4. Files to Change

| File | Change type | Task |
| --- | --- | --- |
| `src/TripMate.Infrastructure/Services/FirebaseAuthService.cs` | Modified (+8/−37) | 1 |
| `tests/TripMate.Application.UnitTests/Services/FirebaseAuthServiceTests.cs` | New | 1 |
| `docker-compose.yml` | Modified (+7) | 2 |
| `.env.example` | Modified (+20/−2) | 2 |
| `README.md` | Modified (+43) | 2 |
| `specs/UC-01-spec.md` | Modified (1 line) | 3 |
| `plans/UC-01-plan.md` | Modified (2 lines) | 3 |

## 5. Production Code Changes

### `src/TripMate.Infrastructure/Services/FirebaseAuthService.cs`

- **Why:** this is the only file containing the insecure fallback; the security decision applies here.
- **What changed:**
  1. Removed `using System.IdentityModel.Tokens.Jwt;`.
  2. Deleted the entire `// 2. Dev Fallback` block (`JwtSecurityTokenHandler`, `CanReadToken`, `ReadJwtToken`, issuer/expiry checks, claim extraction, and the final `return new FirebaseTokenValidationResult(...)`).
  3. Added the fail-closed guard after the Admin SDK block: `throw new InvalidOperationException("Firebase Admin SDK is not configured; Firebase ID token verification is unavailable. Authentication cannot proceed.")`.
  4. Corrected three misleading log messages (constructor ADC failure, constructor init failure, primary-path rejection) to state that verification is unavailable and tokens will be rejected — no message may suggest a fallback exists.
- **What must NOT change in this file:**
  - The credential resolution order (inline JSON → `GOOGLE_APPLICATION_CREDENTIALS` → ADC).
  - The Admin SDK verification path and its claims mapping (`Uid`, `email`, `email_verified`, `name`, `picture`).
  - The empty-token `ArgumentException` contract.
  - The rethrow behavior on SDK verification failure.

### Files explicitly NOT changed (production)

`IFirebaseAuthService.cs` (interface and `FirebaseTokenValidationResult` unchanged), `GoogleTokenValidator.cs` / `IGoogleTokenValidator.cs`, `GoogleAuthCommandHandler.cs`, `RegisterTravelerCommandHandler.cs`, `VerifyEmailCommandHandler.cs`, `AuthController.cs`, `AuthErrorCodes.cs`, `JwtTokenService.cs` (its `JwtSecurityTokenHandler.WriteToken` is legitimate token *issuance*), `ApplicationDbContext.cs`, `DependencyInjection.cs`, `Program.cs`, `Dockerfile`, database schema, FE/Mobile code.

## 6. Test Changes

### `tests/TripMate.Application.UnitTests/Services/FirebaseAuthServiceTests.cs` (new)

- **Why:** no tests previously covered `FirebaseAuthService`; the security invariant (fail-closed, no decode fallback) must be locked in as a regression guard.
- **What:** five xUnit tests (FluentAssertions + Moq, matching project conventions):
  1. `Verify_WhenFirebaseAdminNotConfigured_Throws_ForAnyToken` — forged/unsigned token fixture; asserts a throw, never a trusted result. **Failed (RED) against the vulnerable code** — proving the test exercises the real fallback.
  2. `Verify_WhenFirebaseAdminNotConfigured_NeverAcceptsExpiredOrForgedToken` — second forged fixture; asserts rejection.
  3. `Constructor_WhenNoCredentials_DoesNotThrow_ButLogsUnavailability` — construction succeeds; logger records a Warning/Error about unavailability.
  4. `Verify_WhenTokenEmpty_ThrowsArgument` — preserves the existing empty-token contract.
  5. `Source_FirebaseAuthService_ContainsNoJwtDecodeFallback` — reads the production source (located by walking up to `TripMate.slnx`) and asserts neither `ReadJwtToken` nor `JwtSecurityTokenHandler` appears.
- **What must NOT be done in tests:** no JWT parsing/cryptography re-implementation; no weakening to force failure; no fake credentials. The forged-token fixtures are plain strings — the tests prove the fallback is gone, not that JWT crypto works.
- **Determinism note (documented in the test file):** `FirebaseApp.DefaultInstance` is process-global; no test creates a `FirebaseApp`, so empty-config construction deterministically leaves the SDK unavailable. Revisit if a future test ever creates one.

### Existing tests — unchanged

The 53 baseline tests (handler tests mock `IFirebaseAuthService`) pass unmodified: **58/58 total**.

## 7. Docker Configuration

### `docker-compose.yml` (api service)

- **Why:** after removing the fallback, the container needs a way to receive the service-account JSON so genuine Firebase authentication works in Docker.
- **What:** added `volumes: - ./secrets:/run/secrets:ro` and `environment: GOOGLE_APPLICATION_CREDENTIALS: /run/secrets/tripmate-firebase-admin.json`, plus a comment documenting the fail-closed behavior when the file is missing.
- **What must NOT change:** no credential values in compose; no `Dockerfile` change; no new services; existing env vars and conventions untouched. If `./secrets` is absent, Docker creates an empty directory → `File.Exists` is false → ADC fails → fail closed with a clear log. This is intentional.

### `.env.example`

- **Why:** the previous single commented-out line was outdated and did not explain the requirement.
- **What:** documents that the credential is required for Firebase-token auth, how to obtain it (Firebase console → Project settings → Service accounts → Generate new private key), where to place it (`./secrets/tripmate-firebase-admin.json`, git-ignored), the compose wiring, the inline-JSON alternative (`FIREBASE_SERVICE_ACCOUNT_KEY_JSON` / `Firebase__ServiceAccountKeyJson`), and the local `GOOGLE_APPLICATION_CREDENTIALS` example.
- **What must NOT change:** no secret values, no fake private keys, no real project credentials.

### `README.md`

- **Why:** developers need a setup path; without it, fail-closed looks like a bug.
- **What:** new §9.1 "Firebase Admin credentials" (obtain JSON, place at `secrets/tripmate-firebase-admin.json`, Docker wiring, local env-var/user-secrets/inline-JSON options, credential priority order, fail-closed behavior) and one troubleshooting row in §10 for "Firebase login always rejected".
- **What must NOT change:** unrelated README sections; no credential contents.

## 8. Documentation Changes

### `specs/UC-01-spec.md` (§8, 1 line)

- **Why:** line claimed `FirebaseAuthService` uses "fallback RS256 token verification for local development" — false after the fix.
- **What:** replaced with the Admin-SDK-only / fail-closed description.
- **What must NOT change:** the Google-validator fallback documentation (lines describing `GoogleTokenValidator` / `IGoogleTokenValidator`) — that mechanism still exists and is out of scope.

### `plans/UC-01-plan.md` (BE-UC01-04, 2 lines)

- **Why:** the historical task claimed a "developer fallback using `JwtSecurityTokenHandler`".
- **What:** replaced the two inaccurate claims with the truthful Admin-SDK-only + fail-closed description, marked `(SEC-01)`. Historical structure preserved — the plan was not rewritten.
- **What must NOT change:** BE-UC01-05 (Google Token Validator) and all other Google-fallback references — accurate and out of scope.

## 9. Error Handling

No new error codes or mappings. The fail-closed `InvalidOperationException` propagates through the existing handler catch blocks:

| Flow | Existing mapping (unchanged) |
| --- | --- |
| Register Traveler | `AUTH_TOKEN_INVALID` — "Invalid or expired Firebase authentication token: …" |
| Verify Email | `MSG14` |
| Google Login | existing `IGoogleTokenValidator` chain → `MSG_GOOGLE_TOKEN_INVALID` if that also fails |

Handlers, controllers, `AuthErrorCodes`, and response envelopes have zero diff.

## 10. Security Verification

1. `dotnet test --filter FirebaseAuthServiceTests` → 5/5 (fail-closed + source-conformance).
2. Source search: `ReadJwtToken|JwtSecurityTokenHandler` in `FirebaseAuthService.cs` → **0 matches**; whole `src/` tree → only `JwtTokenService.cs:39` (`WriteToken`, legitimate issuance).
3. Docs search: `fallback RS256|developer fallback|local development fallback` in UC-01 spec/plan → **0 matches** (remaining "fallback" references are the Google validator or unrelated UX flows).
4. `git check-ignore -v secrets/tripmate-firebase-admin.json` → `.gitignore:496:secrets/` (ignored).
5. Manual forged-JWT test (below) → rejected with HTTP 400 `AUTH_TOKEN_INVALID`.

## 11. UC-01 Regression Verification

Manual pass after the fix (all passed): valid Traveler registration; duplicate email; empty Full Name; empty Email; invalid Email; empty Password; confirm-password mismatch; terms unchecked; invalid password; invalid phone; double submit; existing Firebase email; email whitespace; email uppercase.

**Security case:** forged JWT → registration endpoint → HTTP 400:

```json
{
  "success": false,
  "statusCode": 400,
  "message": "Invalid or expired Firebase authentication token: VerifyIdTokenAsync() expects an ID token, but was given a legacy custom token.",
  "data": null,
  "errors": { "code": "AUTH_TOKEN_INVALID" }
}
```

The forged token was **not** accepted. (The expired-token manual case was intentionally skipped; covered by automated test #2 and Admin SDK semantics.)

## 12. Unit Test Verification

```
dotnet test
Passed!  - Failed: 0, Passed: 58, Skipped: 0, Total: 58
```

53 baseline (unchanged) + 5 new `FirebaseAuthServiceTests`. `dotnet build TripMate.slnx` → 0 errors (after the separate POI build fix, see §14).

## 13. Manual Test Verification

- `docker compose config -q` → pass.
- Stack boots: SQL Server healthy → schema applied → API listening; `GET /health` → `{"status":"healthy"}`.
- Without credentials: API boots, startup log states Firebase verification unavailable, Firebase-token endpoints fail closed.
- With credentials (developer's own JSON at `./secrets/tripmate-firebase-admin.json`): genuine Firebase ID tokens authenticate (Admin SDK path).
- Forged JWT rejected (§11).

## 14. Git / Commit Strategy

The working tree contains **three logically separate changesets** — commit them separately, never with `git add .` / `git add -A` (an unrelated untracked file, `specs/UC-04-spec.md`, must not be swept in):

| Changeset | Files | Suggested commit message |
| --- | --- | --- |
| **SEC-01 (this issue)** | `FirebaseAuthService.cs`, `FirebaseAuthServiceTests.cs`, `docker-compose.yml`, `.env.example`, `README.md`, `specs/UC-01-spec.md`, `plans/UC-01-plan.md` | `fix(auth): remove insecure Firebase JWT decode fallback` |
| Baseline merge-artifact fixes (pre-existing, unrelated) | `IApplicationDbContext.cs`, `TestDbContext.cs` | `fix(db): remove duplicate DbSets from merge artifact` |
| POI build fix (unrelated) | `ApiControllerBase.cs` | `fix(api): remove obsolete POI error mappings after upstream revert` |

Stage SEC-01 explicitly, e.g.:

```
git add src/TripMate.Infrastructure/Services/FirebaseAuthService.cs \
        tests/TripMate.Application.UnitTests/Services/FirebaseAuthServiceTests.cs \
        docker-compose.yml .env.example README.md \
        specs/UC-01-spec.md plans/UC-01-plan.md
```

Never commit: `.env`, `secrets/`, any service-account JSON or private key.

## 15. PR Review Checklist

Before opening the PR:

- [ ] `dotnet build TripMate.slnx` → 0 errors.
- [ ] `dotnet test` → 58/58.
- [ ] `git diff --check` → no whitespace errors.
- [ ] `ReadJwtToken` / `JwtSecurityTokenHandler` absent from `FirebaseAuthService.cs` (only `JwtTokenService.cs` `WriteToken` remains in `src/`).
- [ ] `git check-ignore -v secrets/tripmate-firebase-admin.json` → ignored; no secret files in `git status`.
- [ ] Security diff review: no acceptance path when `_firebaseAuth == null`; log messages contain no fallback claims.
- [ ] `IGoogleTokenValidator` / `GoogleTokenValidator` / `GoogleAuthCommandHandler` / handlers / controllers / `AuthErrorCodes` / `JwtTokenService` / `Dockerfile` / schema / FE / Mobile → zero diff.
- [ ] UC-01 valid registration still works (with credentials configured).
- [ ] Forged JWT rejected (HTTP 400 `AUTH_TOKEN_INVALID`).
- [ ] Commit separation per §14 maintained; `specs/UC-04-spec.md` not committed within SEC-01.

## 16. Definition of Done

- [x] `ReadJwtToken()` fallback removed; no decode-based acceptance path in any environment.
- [x] Firebase Admin SDK is the sole trusted verifier; unavailable ⇒ `InvalidOperationException` ⇒ existing error mapping.
- [x] 5 new security tests; 58/58 suite green.
- [x] Docker/local credential configuration wired and documented; secrets git-ignored and never committed.
- [x] UC-01 spec/plan truthful; Google-validator documentation preserved.
- [x] Manual UC-01 regression + forged-token rejection verified.
- [x] No out-of-scope changes (interface, handlers, controllers, error codes, schema, FE/Mobile, `IGoogleTokenValidator`).

---

## Acceptance Criteria / Checklist

- [x] Fail-closed: Admin SDK unavailable → throw for any token (tests 1–2).
- [x] No `ReadJwtToken`/`JwtSecurityTokenHandler` in `FirebaseAuthService` (test 5 + source search).
- [x] Existing empty-token contract preserved (test 4).
- [x] Constructor logs unavailability without throwing (test 3).
- [x] Genuine tokens work when credentials are configured (manual, §13).
- [x] Existing handlers and `IGoogleTokenValidator` unchanged (zero diff).
- [x] 58/58 tests; build 0 errors; compose config valid.

## Risks

| Risk | Status / Mitigation |
| --- | --- |
| Credential-free local runs now fail closed | Intentional (decision #3); README §9.1 + `.env.example` + startup logging guide developers to configure credentials |
| `FirebaseApp.DefaultInstance` process-global static could make tests order-dependent | No test creates a `FirebaseApp`; documented in the test file for future maintainers |
| Source-conformance test depends on repo-relative path | Walks up to `TripMate.slnx`; fails loudly if not found |
| Compose bind-mount of missing `./secrets` creates an empty dir | Accepted: leads to fail-closed with clear log; documented |
| Reviewer conflates SEC-01 with baseline/POI fixes | §14 commit-separation table; PR checklist item |

## Notes for Reviewer

1. Start with the `FirebaseAuthService.cs` diff (§5) — it is the entire security change: fallback deleted, one throw added, three log messages corrected.
2. Confirm the RED evidence: tests 1, 2, and 5 **failed** against the pre-fix code (documented in Task 1), proving they exercise the real vulnerability rather than a mock.
3. Verify the Google-validator files have zero diff — decision #6 keeps that mechanism fully intact.
4. Check commit separation (§14) so SEC-01 remains independently revertable: a single `git revert` of the SEC-01 commit restores the previous behavior exactly (no schema/data/config state involved).
5. The manual forged-token response in §11 is the live proof of fail-closed behavior on the running stack.
