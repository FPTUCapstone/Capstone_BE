# UC-04 — Sign In

## Document Control

| Item | Value |
|---|---|
| Use Case ID | UC-04 |
| Name | Sign In |
| Primary Actor | Guest |
| Priority | Critical |
| Version | **2.0** |
| Status | **Ratified specification — supersedes ALL previous drafts. Implementation is authorized only after team approval of this document.** |
| Decision sources | Ratified checklists **C1–C5** (session 2026-09-13, recorded in `handoff.md` §18) |
| Repository | `Capstone_BE` (FE/Mobile integration impact tracked in §15) |
| Related documents | [`UC-01-spec.md`](UC-01-spec.md) (registration), [`SEC-01-spec.md`](SEC-01-spec.md) (fail-closed Firebase verification), `TEAM_ENGINEERING_RULES.docx`, `Dev_and_CrossReview_Checklist.pdf` |

### 0.1 Spec Authority

1. This document (v2.0) **supersedes every previous UC-04 draft in its entirety**, including the
   earlier unratified draft that required "NEVER automatically create / activate".
2. The ratified checklists **C1–C5** are the decision source for every requirement below.
3. Where this specification conflicts with the SRS, the **SRS wins** (scope authority order:
   approved SRS → team engineering rules → this specification).
4. This file is the **single source of truth** for UC-04 implementation.
5. AI agents and reviewers **must not revert to superseded draft behaviors**, specifically:
   - Google auto-create: draft said *never create* → **ratified D1-A: auto-create Traveler/Active** (BR-02).
   - `PendingEmailVerification` via Google: draft implementation auto-activated → **ratified C2-6: blocked, auto-activate REMOVED** (BR-03/BR-14).
   - `PendingApproval`: implementation historically allowed sign-in → **ratified C2-7: blocked** (BR-05).
   - Administrator Google sign-in: draft said TBD → **ratified D3-A: allowed, role from DB** (BR-18).
   - Google token channels: draft allowed body or Bearer header → **ratified: body-only** (§6.3).
   - Google response shape: **ratified G1-A — aligned with login shape + `isNewAccount`** (BR-17).
   - Firebase infrastructure unavailable: **ratified 503 `auth.firebase_unavailable`** (BR-16).

---

## 1. Scope & Actors

### 1.1 Acceptance Statement

> UC-04 is the BACKEND Sign In of TripMate. A Guest signs in via **Email + Password**
> (existing accounts only) or via **Google using a Firebase ID token** (a Google email that has
> no TripMate account is auto-provisioned with **Role = Traveler, Status = Active** — D1-A).
> The **Backend owns** authentication, account-status checks, role resolution (always from the
> database) and token issuance — tokens are issued only after ALL checks pass; every failure
> issues no token and mutates nothing. The **database is the single source** of
> account/role/status/refresh data (hash-only storage for refresh tokens). **Firebase only
> verifies Google identity.** The client never supplies role or status. Registration, email
> verification, logout, refresh/revoke, forgot password and Phone/OTP are **out of scope**.

### 1.2 Actors

| Role | Participant | Responsibility |
|---|---|---|
| Primary actor | Guest | Submits credentials or a Firebase ID token (unauthenticated in both cases) |
| System | TripMate Backend (ASP.NET Core API) | Owns authentication, status gating, role resolution, token issuance |
| Supporting | TripMate Database (SQL Server) | Single source of account/role/status; stores refresh-token **hashes** |
| Supporting | Firebase Authentication | **Only** verifies the Google identity of the Firebase ID token (Google flow) |
| Channels | FE Web, Mobile | Call the same endpoints; never make sign-in decisions |

FE Web and Mobile are **channels, not actors** — they collect input, send requests and render
results.

---

## 2. Preconditions

1. **Input complete** per method: Email + Password (login) or Firebase ID token (google).
   Missing/invalid input → `400` validation failure **before any database access**.
2. **Dependencies healthy**: database reachable; for the Google flow, Firebase Admin credentials
   configured. Failure of a required dependency is **fail-closed** — the request is rejected,
   never allowed through because verification was impossible (S5, SEC-01).
3. **`EmailVerified` is deliberately NOT a precondition** — email-verification state is checked
   *inside* the flow through the status matrix (BR-03) and the token claims (BR-12).

---

## 3. Main Flows

### 3.1 Email + Password (12 steps)

| # | Step | Rule refs |
|---|---|---|
| 1 | Guest opens Sign In | §1 |
| 2 | Guest enters Email + Password | §2 |
| 3 | Client sends `POST /api/v1/auth/login` `{email, password}` — no Authorization header | §6 |
| 4 | Backend validates input → invalid/missing → **400**, no DB access | C2-1 |
| 5 | Backend normalizes email (trim + lowercase); password kept **byte-for-byte** | §6.2 |
| 6 | Backend searches `dbo.Users` by normalized email → not found → **401** `auth.invalid_credentials` | BR-01 |
| 7 | Backend verifies password (PBKDF2) → mismatch → **401** `auth.invalid_credentials`; `PasswordHash` null → **401**, same code + message | BR-01, S8 |
| 8 | Status check (§8): `Active`/`Rejected` pass; others blocked with their 403 codes | §8 |
| 9 | On pass: persist refresh-token row (**SHA-256 hash**, 7-day expiry) + update `LastLoginAtUtc` — inside a transaction | BR-10, BR-15 |
| 10 | Issue Access Token: JWT HS256, **15 min**, claims `sub`, `nameidentifier`, `email`, `role` (from DB), `jti` | BR-10 |
| 11 | Return `200` envelope with `AuthResponseDto` (§7.2) | §6.1 |
| 12 | Client stores tokens and routes by the **role returned by the backend** | §1.2 |

**Invariant order:** validate → authenticate → status gate → persist session → issue token →
respond. Any failed step ends the flow with zero mutation.

### 3.2 Google Sign In (14 steps)

| # | Step | Rule refs |
|---|---|---|
| 1 | Guest opens Sign In, selects "Continue with Google" | §1 |
| 2 | (Client) Terms consent gate **before** the popup | §15 (follow-up) |
| 3 | (Client) Google popup → Firebase returns the **Firebase ID Token** | — |
| 4 | Client sends `POST /api/v1/auth/google` `{ "idToken": "..." }` — **body-only**; Bearer header is not an input channel | §6.3, C4-1 |
| 5 | Backend validates presence → missing → **400** `AUTH_TOKEN_MISSING` | §7.3 |
| 6 | Backend verifies the token with the **Firebase Admin SDK** (sole trusted verifier, fail-closed, SEC-01) → invalid/expired → **401** `AUTH_TOKEN_INVALID` | S5, S7 |
| 7 | **BR-11**: nested claim `firebase.sign_in_provider` must equal `"google.com"` → missing/different → **401** `AUTH_TOKEN_INVALID` (fail-closed) | BR-11 |
| 8 | **BR-12**: claim `email_verified` must be `true` → `false` **or MISSING** → **403** `MSG_EMAIL_NOT_VERIFIED` (fail-closed; never default to true) | BR-12 |
| 9 | Backend normalizes email (trim + lowercase) | §6.2 |
| 10 | Backend searches `dbo.Users`: **NOT FOUND → auto-provision (BR-02)**: `User { Email, FullName (from token), AvatarUrl (from token), Role = Traveler, Status = Active, EmailVerifiedAtUtc = now }`, `isNewAccount = true` — only ever Traveler/Active, never TourOperator/Administrator. **FOUND → status check (§8)**: `Active`/`Rejected` pass; `PendingEmailVerification` → **403** `MSG_UNVERIFIED` (auto-activate removed, C2-6); `PendingApproval` → **403** `auth.account_pending_approval` (C2-7); `Locked`/`Inactive` → **403**. **Invariant (BR-14): Sign In never changes the Status of an existing account (Login ≠ Activation).** On pass: backfill avatar **only if empty** (never overwrite) + update `LastLoginAtUtc` | BR-02, BR-14, §8 |
| 11 | Persist refresh-token row (SHA-256 hash, 7-day expiry) | BR-10, BR-15 |
| 12 | Issue Access Token (JWT 15 min, role from DB) | BR-10 |
| 13 | Return `200` envelope with the G1-A response shape (§7.3) | BR-17 |
| 14 | Client stores tokens and routes by role | §1.2 |

**Invariant order (P5):** Firebase verify → BR-11 → BR-12 → normalize → DB lookup →
(auto-provision **or** status gate) → persist → issue → respond. Claims checks are NEVER skipped
or reordered before the lookup.

---

## 4. Alternative / Exception Flows

### 4.1 Email + Password

| ID | Case | Behavior |
|---|---|---|
| A1 | Missing/invalid input | **400** ValidationProblemDetails. No DB access |
| A2 | Email not found | **401** `auth.invalid_credentials` |
| A3 | Wrong password | **401** `auth.invalid_credentials` |
| A4 | `PasswordHash` null (no password set) | **401** `auth.invalid_credentials` |
| A5 | `PendingEmailVerification` | **403** `MSG_UNVERIFIED` |
| A6 | `PendingApproval` | **403** `auth.account_pending_approval` |
| A7 | `Locked` | **403** `auth.account_locked` |
| A8 | `Inactive` | **403** `auth.account_inactive` |
| A9 | Database unreachable | **500** generic ProblemDetails |
| A10 | Unexpected error | **500** generic ProblemDetails |

**A2/A3/A4 must return IDENTICAL security-relevant responses** (status, title/detail,
`errorCode`; dynamic fields such as `traceId` are excluded) — the API must not reveal whether an
email exists.

### 4.2 Google

| ID | Case | Behavior |
|---|---|---|
| B1 | `idToken` missing/empty | **400** `AUTH_TOKEN_MISSING` (ProblemDetails shape) |
| B2 | Token invalid/expired/unverifiable | **401** `AUTH_TOKEN_INVALID` — no raw exception details |
| B3 | Firebase unavailable (SDK not initialized / infra down) | **503** `auth.firebase_unavailable` — fail-closed; email login unaffected (S5) |
| B4 | `firebase.sign_in_provider` ≠ `google.com` or missing | **401** `AUTH_TOKEN_INVALID` (BR-11) |
| B5 | `email_verified` false or missing | **403** `MSG_EMAIL_NOT_VERIFIED` (BR-12) |
| B6 | Token contains no email | **401** `AUTH_TOKEN_INVALID` |
| B7 | Email not found | **Auto-provision** — main flow (BR-02); concurrent first sign-ins resolved via unique index `UX_Users_Email` → re-fetch and continue status gate (or `409`) |
| B8 | Email found, status gate | Same codes as A5–A8 — **one shared matrix** |

### 4.3 Cross-cutting failure rules

1. **Zero mutation**: no access token, no refresh-token row, no `LastLoginAtUtc`, no avatar
   backfill, no status change on ANY failure path (BR-10).
2. All failures are RFC-7807 ProblemDetails with an `errorCode` extension (two-shapes rule, §6.1);
   validation failures use normalized camelCase error keys.
3. No information disclosure: never reveal email existence, lock reasons, stack traces or
   internals (S8–S10).

---

## 5. Business Rules

> **Catalog:** BR-01 … BR-12 and BR-14 … BR-18 = **17 active rules**.
> **BR-13 is intentionally RETIRED/RESERVED** (its old content "never auto-create" was superseded
> by BR-02 per D1-A). Do not renumber; do not reuse the number.

| ID | Rule |
|---|---|
| **BR-01** | Email+Password signs in **existing accounts only**. Unknown email, wrong password and `PasswordHash` null return the **same** generic `401 auth.invalid_credentials` response. |
| **BR-02** | Google provisioning follows UC-01 BR-06: an email with no TripMate account is auto-provisioned as **Role = Traveler, Status = Active**, `isNewAccount = true`. Auto-provisioning **never** produces TourOperator or Administrator. |
| **BR-03** | `EmailVerified` is checked inside the flow (not a precondition). An account with `Status = PendingEmailVerification` is **blocked on both methods** with `403 MSG_UNVERIFIED`. |
| **BR-04** | Role and Status are resolved **only from the database**. Requests carry no role/status and the server ignores any client-supplied values (M9). |
| **BR-05** | `PendingApproval` blocks sign-in on **both methods**: `403 auth.account_pending_approval`. Only UC-50 approval transitions the account to `Active`. |
| **BR-06** | A Tour Operator whose application is `Rejected` **is allowed to sign in** (both methods) provided all other checks pass. The account Status remains `Rejected` — it is never automatically changed to `Active`. The operator workspace shows the rejection reason and the resubmit path (UC-03). |
| **BR-07** | `Locked` blocks sign-in on both methods: `403 auth.account_locked`. No mutation. Only an administrator unlocks. |
| **BR-08** | `Inactive` blocks sign-in on both methods: `403 auth.account_inactive`. No mutation. Reactivation is a separate future use case. |
| **BR-09** | Operator **workspace/business endpoints must check `OperatorProfile.ApprovalStatus == Approved`** in addition to the role claim — a `Role = TourOperator` claim alone never grants business access (a rejected operator still holds that role). Enforced in all future operator use cases; not testable within UC-04 itself. |
| **BR-10** | Tokens are issued **only after every check passes**. Every failure path: no access token, no refresh-token row, no `LastLoginAtUtc`, no status change. |
| **BR-11** | The Firebase ID token must carry the nested claim `firebase.sign_in_provider = "google.com"`. Missing or different → `401 AUTH_TOKEN_INVALID`, fail-closed. |
| **BR-12** | The Firebase ID token must carry `email_verified = true`. `false` **or MISSING** → `403 MSG_EMAIL_NOT_VERIFIED`, fail-closed. Never default a missing claim to `true`. |
| ~~BR-13~~ | **RETIRED (reserved).** Superseded by BR-02. Do not reuse this number. |
| **BR-14** | Sign In **never activates an existing account**. Google auto-provisioning of a previously **nonexistent** account is **creation, not activation**; an existing account's Status is **never changed by Sign In**. *Boundary: BR-02 applies only when the DB lookup finds no user; BR-14 applies to every existing user regardless of method.* |
| **BR-15** | Session persistence (refresh-token row + `LastLoginAtUtc`) happens **only on success**, inside a transaction (`ExecuteInTransactionAsync`); token generation and the response follow the commit. No partially-created sessions. |
| **BR-16** | Firebase unavailability rejects Google requests with `503 auth.firebase_unavailable`. Firebase is **not** a dependency of the email+password flow — per-flow failure domains. |
| **BR-17** | The Google response shape equals the login shape plus `isNewAccount` (G1-A): `userId, email, fullName, role, status, accessToken, refreshToken, accessTokenExpiresAtUtc, isNewAccount`. `role` always from the DB. Additive changes only. |
| **BR-18** | An existing Administrator account may sign in with Google (D3-A). Role is always resolved from the DB — Google never grants or changes a role. |

---

## 6. API Conventions

### 6.1 Two response shapes (only these exist)

```
HTTP 200              → ApiResponse<T> { success, statusCode, message, data, errors }
HTTP 400/401/403/500  → RFC-7807 ProblemDetails (+ errorCode extension)
```

There is **no** `{ "success": false, ... }` error envelope and **no** 200-failed state — the HTTP
status is the single source of truth. (Note: repo `AGENTS.md` §2.3 discourages an envelope; the
team checklist + this spec + the implemented auth endpoints keep the envelope — see §16.)

### 6.2 Field normalization (per-field, never blanket)

- `email` → trim + **lowercase** by the backend (clients are not required to normalize);
- `password` → **NOT trimmed, NOT normalized** — compared byte-for-byte as sent; only non-empty
  is checked;
- `idToken` → never modified.
- Password complexity is **not re-checked at login** (registration policy); wrong complexity is a
  401, not a 400.

### 6.3 Google token channel — BODY-ONLY (team decision)

```
POST /api/v1/auth/google
Content-Type: application/json
{ "idToken": "..." }
```

`Authorization: Bearer` is **not an input channel** for UC-04. Verified: no current client breaks
(FE sends both, Mobile sends body).

### 6.4 Unknown fields

The server **ignores** request fields not listed in the contract (M9). This relies on the default
ASP.NET Core/System.Text.Json unknown-member behavior — implementers must **verify** the current
JSON options and **must not** change global serialization settings for UC-04.

---

## 7. API Contract

### 7.1 Endpoints

| Endpoint | Auth | Actor |
|---|---|---|
| `POST /api/v1/auth/login` | Anonymous | Guest |
| `POST /api/v1/auth/google` | Anonymous | Guest |

### 7.2 `POST /api/v1/auth/login`

**Request**

```json
{ "email": "minh.nguyen@example.com", "password": "P@ssw0rd123" }
```

| Field | Type | Required | Normalization |
|---|---|---|---|
| `email` | string | ✅ | trim + lowercase by backend |
| `password` | string | ✅ | byte-for-byte, no trim |

**Response 200** (`data` = `AuthResponseDto`)

```json
{
  "success": true, "statusCode": 200, "message": "Login successful.",
  "data": {
    "userId": 7,
    "email": "minh.nguyen@example.com",
    "fullName": "Nguyễn Văn Minh",
    "role": "Traveler",
    "status": "Active",
    "accessToken": "eyJhbGciOiJIUzI1NiIs...",
    "refreshToken": "<opaque, ~88 Base64 chars>",
    "accessTokenExpiresAtUtc": "2026-09-13T10:15:00Z"
  },
  "errors": []
}
```

| Field | Contract |
|---|---|
| `userId` | long, DB id |
| `email` | **string (non-null in UC-04)** — the normalized account email. The shared C# DTO being `string?` is a DTO artifact, not a UC-04 capability |
| `fullName` | string |
| `role` | string enum `Traveler`/`TourOperator`/`Administrator` — from DB |
| `status` | string enum — only `Active` or `Rejected` can reach a 200 |
| `accessToken` | JWT HS256, 15 min, claims `sub`/`nameidentifier`/`email`/`role`/`jti` |
| `refreshToken` | opaque token generated from **64 cryptographically secure random bytes**, returned once; BE persists only the SHA-256 hash |
| `accessTokenExpiresAtUtc` | ISO-8601 UTC |

**Errors**: A1–A10 (§4.1). The three 401 cases return identical security-relevant bodies.

**5xx invariants**: no exception/stack leaks; no `accessToken`; no `refreshToken`; no
password/hash/Firebase internals. Session persistence is transactional (BR-15).

### 7.3 `POST /api/v1/auth/google`

**Request** (body-only, §6.3)

```json
{ "idToken": "eyJhbGciOiJSUzI1NiIs..." }
```

**Response 200** (`data` = `GoogleAuthResponse`, **G1-A aligned with login + `isNewAccount`**)

```json
{
  "success": true, "statusCode": 200, "message": "Google authentication successful.",
  "data": {
    "userId": 12,
    "email": "lan@gmail.com",
    "fullName": "Lan Pham",
    "role": "Traveler",
    "status": "Active",
    "accessToken": "eyJhbGciOiJIUzI1NiIs...",
    "refreshToken": "<opaque, ~88 Base64 chars>",
    "accessTokenExpiresAtUtc": "2026-09-13T10:15:00Z",
    "isNewAccount": true
  },
  "errors": []
}
```

- `isNewAccount = true` ⟹ the account was auto-provisioned this call ⟹ `status = "Active"`,
  `role = "Traveler"` (BR-02).
- `status = "Rejected"` (existing rejected Tour Operator) is a valid 200 per BR-06.

**Errors**: B1–B8 (§4.2) — `400 AUTH_TOKEN_MISSING`, `401 AUTH_TOKEN_INVALID` (incl. BR-11),
`403 MSG_EMAIL_NOT_VERIFIED` (BR-12), `403` status gates, **`503 auth.firebase_unavailable`**,
`500` generic. 5xx invariants identical to §7.2.

### 7.4 Error catalog (complete for UC-04)

| # | errorCode | HTTP | Method(s) | Meaning | Message (ProblemDetails `title`) | Status |
|---|---|---|---|---|---|---|
| 1 | *(ValidationProblemDetails)* | 400 | login | input invalid | *(per-field validation messages)* | existing |
| 2 | `AUTH_TOKEN_MISSING` | 400 | google | body missing `idToken` | "Google ID token is required." | existing |
| 3 | `auth.invalid_credentials` | 401 | login | 3 identical credential failures | "Invalid email or password." | existing |
| 4 | `AUTH_TOKEN_INVALID` | 401 | google | verify fail / BR-11 fail / no email claim | "Invalid Google ID token." | existing |
| 5 | `MSG_UNVERIFIED` | 403 | both | `PendingEmailVerification` | "Email has not been verified." | existing |
| 6 | `auth.account_pending_approval` | 403 | both | `PendingApproval` | "Account is pending approval." | **NEW** |
| 7 | `auth.account_locked` | 403 | both | `Locked` | "Account is locked." | existing |
| 8 | `auth.account_inactive` | 403 | both | `Inactive` | "Account is inactive." | existing |
| 9 | `MSG_EMAIL_NOT_VERIFIED` | 403 | google | BR-12 fail | "Google account email has not been verified." | existing |
| 10 | `auth.firebase_unavailable` | **503** | google | Firebase infra down | "Google authentication service is temporarily unavailable." | **NEW** |
| 11 | *(generic ProblemDetails)* | 500 | both | unexpected error | "An unexpected error occurred." | existing |

Error codes live in `AuthErrorCodes.cs` as **code constants with technical messages** (no
`dbo.Messages` change). Human-readable text is localized **client-side** (FE `authErrorMapper`,
Mobile `_messageForCode`) — both mappers must add codes #6 and #10. `ApiControllerBase.HandleFailure`
must gain a 503 branch and the pending-approval mapping (§12-#9).

---

## 8. Status Matrix (final, ratified)

| Status | Email + Password | Google | Error code when blocked |
|---|---|---|---|
| `Active` | ✅ allow | ✅ allow | — |
| `PendingEmailVerification` | ❌ 403 | ❌ 403 (auto-activate REMOVED) | `MSG_UNVERIFIED` |
| `PendingApproval` | ❌ 403 | ❌ 403 | `auth.account_pending_approval` |
| `Rejected` (Tour Operator) | ✅ allow (status unchanged) | ✅ allow | — |
| `Locked` | ❌ 403 | ❌ 403 | `auth.account_locked` |
| `Inactive` | ❌ 403 | ❌ 403 | `auth.account_inactive` |

Google-only pre-matrix gates: BR-11, BR-12.

---

## 9. Security

| # | Invariant |
|---|---|
| S1 | JWT signing key from env (`JWT_SIGNING_KEY`), never committed; HS256 |
| S2 | Password hashing PBKDF2 (ASP.NET Identity PasswordHasher); never plaintext, never logged |
| S3 | Refresh token: opaque, generated from 64 cryptographically secure random bytes, returned once; BE stores only the SHA-256 hash |
| S4 | Firebase Admin credentials mounted read-only (`GOOGLE_APPLICATION_CREDENTIALS`); never committed |
| S5 | **Firebase unavailable → `503 auth.firebase_unavailable` for the GOOGLE flow only.** Firebase is NOT a dependency of email+password login (per-flow failure domains). Fail-closed — no pass-through path |
| S6 | BR-11/BR-12 claims MISSING → treated as failed; never default to true |
| S7 | No unverified decode/fallback paths (SEC-01 + D2-A) — including the removed raw-Google fallback |
| S8 | Three credential-failure cases of email login return identical security-relevant responses; `AUTH_TOKEN_INVALID` is a separate token-failure class of the google flow |
| S9 | `Locked` returns a generic message — no reason, actor or timestamps |
| S10 | 5xx: no stack/internals/password/hash/Firebase details |
| S11 | Never log passwords, raw access/refresh tokens, raw idTokens or Firebase secrets. Serilog must not log auth request bodies. **Verification:** integration test asserting log output contains no secret substrings + PR config checklist |
| S12 | Client-sent role/status/token fields are ignored (M9 + §6.4 caveat) |
| S13 | Production requires HTTPS; CORS whitelists explicit per-environment origins. `localhost:3000/3001/3005` are DEV-ONLY config — never copied to production |
| S14 | Access token 15 min; refresh 7 days |

**Out-of-scope security notes:** rate limiting/auto-lockout → SEC backlog (recommended
post-UC-04); timing side-channel on unknown email → known limitation, optional dummy-hash
hardening.

---

## 10. NFR

| # | Requirement / Target |
|---|---|
| N1 | **Targets (not yet met — measured after implementation; SRS wins if it specifies its own):** login success p95 ≤ 500ms; login failure p95 ≤ 300ms; google p95 ≤ 800ms; ≤ 3 DB round-trips on the three normal success paths (login-existing, google-existing, google-auto-create); race recovery exempt |
| N2 | Dependency isolation per S5; BE stateless (JWT) — horizontally scalable; sessions live in client + `RefreshTokens` |
| N3 | Structured Serilog logging per auth request: outcome (success/errorCode), userId when known, latency — **measurement mechanism: structured log properties** (metrics library deferred). Distinguishable 401 vs 503 counts. No AuditLog rows for sign-in (session trace = `LastLoginAtUtc` + `RefreshTokens`) |
| N4 | Contract changes additive only (Swagger diff gate); zero DB migration; stable error codes; lifetimes/origins config-driven; no new architectural patterns (RULE 07) |
| N5 | Known limitations: no refresh/logout endpoint (client cannot auto-renew); no rate limiting; timing side-channel — all accepted/ratified, listed in §13 |

---

## 11. Test Plan

**Coverage criterion:** every ratified invariant has ≥ 1 automated regression guard AND all
existing relevant suites remain green. (~30 new/adjusted tests is an estimate, not a criterion.)
**Merge principle:** no ratified rule without an automated guard; no merge while relevant
existing tests are red.

### A. Application unit — `LoginCommandHandler`

| Test | Guards |
|---|---|
| Active success → tokens + refresh row + LastLogin updated | BR-10 |
| Unknown email / wrong password / PasswordHash null → identical `auth.invalid_credentials` | BR-01, S8 |
| `PendingEmailVerification` → `MSG_UNVERIFIED`, zero mutation | BR-03 |
| `PendingApproval` → `auth.account_pending_approval`, zero mutation | BR-05 🔧 |
| `Locked` / `Inactive` → 403, zero mutation | BR-07/08 |
| `Rejected` → allowed, status unchanged | BR-06 |
| Success path transactional | BR-15 🔧 |

### B. Application unit — `GoogleAuthCommandHandler`

| Test | Guards |
|---|---|
| Unknown email → auto-provision Traveler/Active, `isNewAccount=true` (assert Role) | BR-02 |
| Concurrent first sign-in (unique violation) → re-fetch/409, no crash | BR-02 (race) 🔧 |
| Existing Active: avatar empty → backfilled; avatar present → NOT overwritten | BR-14 (P10) |
| `PendingEmailVerification` → 403 + **status unchanged** (expectation flipped — deliberate) | BR-03/14 🔧 |
| `PendingApproval` → 403 + status unchanged | BR-05 🔧 |
| `Locked`/`Inactive` → 403 + zero mutation | BR-07/08 |
| `Rejected` → allowed, status unchanged | BR-06 |
| BR-11: provider claim ≠ `google.com` / missing → 401 | BR-11 🔧 |
| BR-12: `email_verified` false **and MISSING** → 403 | BR-12 🔧 |
| Token without email → 401 | B6 |
| Firebase verify fail → 401; Firebase unavailable → typed exception → 503 | S5 🔧 |
| Existing Administrator via Google → 200, `role = Administrator` | BR-18 🔧 |

### C. Infrastructure unit

| Test | Guards |
|---|---|
| `FirebaseAuthService` exposes `sign_in_provider` + `email_verified` from a sample token | BR-11/12 🔧 |
| `JwtTokenService`: 64-byte refresh entropy; SHA-256 hash match; 15-min access; `role` claim from DB | S3/C2-9 |
| Validators: missing fields → validation failure | C2-1 |

### D. API integration

| Test | Guards |
|---|---|
| `POST /auth/login` 200 — envelope with all 8 fields, string enums | C4-2 |
| 3×401 — identical **security-relevant fields** (dynamic fields excluded) | S8 🔧 |
| `POST /auth/google` 200 — G1-A shape (9 fields) | BR-17 🔧 |
| Google 400 missing token — ProblemDetails + errorCode shape | §12-#8 🔧 |
| 503 Firebase unavailable — errorCode, no tokens | BR-16 🔧 |
| Parameterized failure theory: **every** failure path → `LastLoginAtUtc` unchanged + no new RefreshTokens row + (google) avatar unchanged | BR-10/15 🔧 |
| OpenAPI document: google request body-only `idToken`; G1-A fields; string enums; `errorCode` extension; NO Bearer parameter; no out-of-scope endpoints | C4 🔧 |

### E. Client follow-up (NOT part of UC-04 BE)

FE `authErrorMapper` + retry UI for #6/#10; Mobile `SessionResponseDto` G1-A parse +
`_messageForCode`; terms gate tests (separate task).

### F. Zero Regression gate

All existing suites stay green (Application ~123, API 30 passed/3 skipped, Infrastructure 3).
Tests whose expectations were flipped (auto-activate → 403) are **deliberate changes ratified by
this spec** — the PR must state: *"Deliberate contract alignment: Google Sign In no longer
auto-activates PendingEmailVerification; the account remains unchanged and receives
MSG_UNVERIFIED."*

---

## 12. Implementation Notes

**Code changes (all verified against current source):**

| # | Change | File |
|---|---|---|
| 1 | Remove raw-Google token fallback | `GoogleAuthCommandHandler` |
| 2 | Add BR-11 check (nested provider claim) | `GoogleAuthCommandHandler` |
| 3 | Add BR-12 check (email_verified, fail-closed) | `GoogleAuthCommandHandler` |
| 4 | Remove PendingEmailVerification auto-activation | `GoogleAuthCommandHandler` |
| 5 | Add PendingApproval block | `GoogleAuthCommandHandler` |
| 6 | Align `GoogleAuthResponse` with G1-A — **PRESERVE** `userId, status, accessToken, refreshToken, isNewAccount`; **ADD** `role`, `email`, `fullName`, `accessTokenExpiresAtUtc` (additive, Swagger updated) | `GoogleAuthResponse` |
| 7 | Update switch + doc comment per C2-7 | `LoginCommandHandler` |
| 8 | **CONFIRMED non-conforming**: the 400 `AUTH_TOKEN_MISSING` path returns an ApiResponse envelope — refactor to ProblemDetails + errorCode; remove the `ExtractBearerToken()` fallback | `AuthController` |
| 9 | `HandleFailure` switch audit: add `auth.account_pending_approval` → 403, `auth.firebase_unavailable` → 503; verify all google codes map 401/403 (default `_ => 400` must not swallow them) | `ApiControllerBase` |
| 10 | Extend `FirebaseAuthService` to expose `sign_in_provider` + `email_verified`; typed `FirebaseUnavailableException` for the 503 path | `FirebaseAuthService` |
| 11 | Wrap session persistence in `ExecuteInTransactionAsync` | `LoginCommandHandler`, `GoogleAuthCommandHandler` |

**Order (TDD):** red tests for the login cluster (BR-03/05/07/08/10) → green → google cluster
(BR-11/12/14/16/18) → green → contract changes (§7) → Swagger diff.

**Do NOT touch:** database schema, `dbo.Messages`, `dbo.AuthProviders`, any non-auth endpoint.

**PR-note (mandatory):** *"Deliberate contract alignment: Google Sign In no longer auto-activates
PendingEmailVerification; the account remains unchanged and receives MSG_UNVERIFIED."*

---

## 13. Out of Scope & Deferred

| Item | Belongs to |
|---|---|
| Registration + email verification + resend | UC-01 |
| Logout, refresh/revoke endpoints | Separate session-architecture decision |
| Operator application / resubmit | UC-02 / UC-03 / UC-50 / UC-51 |
| Forgot password, Phone/OTP | Separate future use cases |
| Client token storage (FE localStorage) | Session-architecture decision |
| `dbo.AuthProviders` table | Deferred enhancement (D4-A) — Google identity matched by verified email |
| Rate limiting / auto-lockout | SEC backlog |
| Timing hardening (dummy-hash) | Optional |

---

## 14. Traceability Matrix

| Rule | Decision source | Spec section | Test guard | Code change |
|---|---|---|---|---|
| BR-01 | C2-6/S8 | §5, §4.1 | A identical-401 | — |
| BR-02 | D1-A, C2-6 | §5, §3.2-10 | B auto-create + race | — |
| BR-03 | C2-6 | §8 | A/B pending-unverified | #4 |
| BR-04 | C1 M9 | §6.4 | D openapi/validation | — |
| BR-05 | C2-7 | §8 | A/B pending-approval | #5, #7, #9 |
| BR-06 | C2-8 | §8 | A/B rejected | — |
| BR-07 | C2-4 | §8 | A/B locked | — |
| BR-08 | C2-5 | §8 | A/B inactive | — |
| BR-09 | C5-4 F2 | §5 | **future operator UCs + review checklist** (not testable in UC-04) | future |
| BR-10 | C1 M5/C2-9 | §3, §4.3 | A/B zero-mutation + D theory | — |
| BR-11 | C3-2 v2 (P2) | §3.2-7 | B BR-11 | #2, #10 |
| BR-12 | C3-2 v2 (P3) | §3.2-8 | B BR-12 | #3, #10 |
| BR-14 | C2-6/P6 | §3.2-10 | B status-unchanged | #4 |
| BR-15 | C4-2 | §3-9/11 | D theory + A transactional | #11 |
| BR-16 | B3/S5 | §4.2-B3 | D 503 | #9, #10 |
| BR-17 | G1-A | §7.3 | D G1-A shape | #6 |
| BR-18 | D3-A | §5 | B administrator | — |

---

## 15. Cross-Document Impact (FE/Mobile)

| Change | BE | FE | Mobile |
|---|---|---|---|
| G1-A response fields | `GoogleAuthResponse` + Swagger | `authApi.googleAuth()` type + role routing | `SessionResponseDto` (optional parse — confirm) |
| Body-only `idToken` | remove `ExtractBearerToken()` | already sends body | already sends body |
| New codes #6/#10 | `AuthErrorCodes` + 503 branch | `authErrorMapper` + Retry UI | `_messageForCode` + Retry UI |
| Terms gate before `/auth/google` | — | required (follow-up) | required (follow-up) |

---

## 16. Required Amendments Elsewhere (tracked, not part of UC-04 code)

1. **UC-01 BR-06** — remove "activate account if pending" (contradicts BR-03); replace with
   "reject with MSG_UNVERIFIED". Also remove the nonexistent `Suspended` status from its enum
   table. **Must complete before or with UC-04 implementation.**
2. **AGENTS.md §2.3** — the "no envelope" note conflicts with the ratified envelope + team
   checklist; update in a docs pass (reported, not silently fixed).
3. FE/Mobile follow-ups listed in §15.

---

*End of specification — UC-04 Sign In, v2.0 (ratified decision set C1–C5, 2026-09-13).*
