# UC-04 — Sign In

> Latest target: Revision 3 at the end of this file, amended with approved legacy resolver and narrowed core scope. It supersedes conflicting v2.x Web-session and application-state rules. Historical sections are retained as implementation history. Final status (2026-09-15): core W01/T16 contract APPROVED/DONE; runtime implemented — Web sign-in/Admin/Google/verify-email (T17-T20), core acceptance (T21) and Web refresh/session restoration (T22/S01) are DONE; Sign Out/revocation (T23) remains a separate UC. UC-04 SIGN IN: DONE — see "Final delivery status" at the end of this file.

## Revision 2.1 — Administrator email/password only (approved 2026-09-14)

The user approved this revision and its implementation plan on 2026-09-14. It replaces
v2.0 BR-18/D3-A (previously allowed Administrator Google sign-in). All other v2.0 rules
remain in force. The relevant flow, error, test and traceability entries below are aligned.

### Scope and revised BR-18

Administrator may obtain new sessions through email/password only. A Google Firebase token
resolving to an existing Administrator must be rejected before user mutation or session
persistence. Traveler/TourOperator Google sign-in remains supported; unknown Google email
still creates Traveler/Active, never Administrator. Existing sessions are not revoked.

The rejection is HTTP **403 ProblemDetails**, errorCode
`auth.admin_google_sign_in_disabled`, title:
"Administrator accounts must sign in with email and password."
Add a matching AuthErrorCodes constant and ApiControllerBase.HandleFailure mapping.

### Flow and precedence

1. Retain token verification, provider, verified-email and email-presence checks.
2. Resolve the user by normalized email, then apply existing account-status gates.
3. If status permits sign-in but role is Administrator, return the new 403 failure.
4. Run the same status/role gate after re-fetching the account following DbUpdateException.
5. Reject before avatar backfill, login/update timestamp mutation and session persistence.
   Normal lookup rejection must not generate either access or refresh tokens. The retry path
   may already have generated a raw refresh value, but must not persist it or issue a JWT.

### Alternative session path: verify-email

Audit identified that `/auth/verify-email` independently issues session tokens from a verified
Firebase identity. It must reject an Administrator token with sign_in_provider `google.com`
using the same 403/code, before activation or session issuance. Retain existing token/email
validation and Locked/Inactive precedence. Existing non-Google verification behavior is
unchanged. This explicitly scoped verification guard prevents bypassing the revised BR-18.

### Web impact and limits

- `/admin/login` shows email/password only; remove any admin Google button/gate.
- Public Google sign-in maps the new code to a message directing admin to email/password.
- Admin Web currently simulates password success. Replacing that simulation is part of the
  separately approved FE redesign, not this removal task.
- Broader FE spec completion still waits for all user checklists; this revision supersedes
  the earlier checklist approval to show Google at the admin login.
- No database/schema change, endpoint removal, logout/refresh feature or session revocation.
- Administrators without a usable password require recovery/provisioning; no process is
  invented here. Mobile receives the new BE rejection; its UI changes are outside scope.

### Acceptance and regression coverage

- Normal and concurrency re-fetch Administrator Google rejection: 403/code, no persisted
  session, unchanged user/avatar/timestamps. Normal rejection generates no tokens.
- Verify-email Administrator Google rejection: no activation, mutation or session issuance.
- API integration tests verify HTTP mapping and ProblemDetails errorCode for both endpoints.
- Administrator email/password remains successful; non-admin Google auto-provision/existing
  user flows and all status/claim/infrastructure gates retain their behavior.
- Replace the v2.0 Administrator Google success expectation; update contradictory code
  comments and rule references as part of implementation.
- Run focused tests followed by full `dotnet test` and applicable FE tests/lint/typecheck;
  review authorization paths and specification compliance separately.

### Audit baseline (2026-09-14)

`dotnet test TripMate.slnx --no-restore --verbosity minimal`: **232 passed, 0 failed,
8 skipped**. The skipped checks include real SQL Server Google concurrency; report missing
environment coverage explicitly. Final implementation results are recorded in the UC-04 plan.

---

## Document Control

| Item | Value |
|---|---|
| Use Case ID | UC-04 |
| Name | Sign In |
| Primary Actor | Guest |
| Priority | Critical |
| Version | **2.1** |
| Status | **User-approved specification and implementation plan (2026-09-14); team PR review pending.** |
| Decision sources | Ratified checklists **C1–C5** (2026-09-13) + user-approved Administrator Google removal revision (2026-09-14) |
| Repository | `Capstone_BE` (FE/Mobile integration impact tracked in §15) |
| Related documents | [`UC-01-spec.md`](UC-01-spec.md) (registration), [`SEC-01-spec.md`](SEC-01-spec.md) (fail-closed Firebase verification), `TEAM_ENGINEERING_RULES.docx`, `Dev_and_CrossReview_Checklist.pdf` |

### 0.1 Spec Authority

1. This document (v2.1) **supersedes every previous UC-04 draft in its entirety**, including the
   earlier unratified draft that required "NEVER automatically create / activate".
2. The ratified checklists **C1–C5** plus the approved revision 2.1 are the decision sources.
3. Where this specification conflicts with the SRS, the **SRS wins** (scope authority order:
   approved SRS → team engineering rules → this specification).
4. This file is the **single source of truth** for UC-04 implementation.
5. AI agents and reviewers **must not revert to superseded draft behaviors**, specifically:
   - Google auto-create: draft said *never create* → **ratified D1-A: auto-create Traveler/Active** (BR-02).
   - `PendingEmailVerification` via Google: draft implementation auto-activated → **ratified C2-6: blocked, auto-activate REMOVED** (BR-03/BR-14).
   - `PendingApproval`: implementation historically allowed sign-in → **ratified C2-7: blocked** (BR-05).
   - Administrator Google sign-in: v2.0 D3-A allowed it → **v2.1 BR-18 blocks it; email/password only**.
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
> verification (except the revision 2.1 anti-bypass guard), logout, refresh/revoke,
> forgot password and Phone/OTP are **out of scope**.

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
(auto-provision **or** status gate → Administrator method gate) → persist → issue → respond. Claims checks are NEVER skipped
or reordered before the lookup.

For an existing Administrator, step 10 ends with **403
`auth.admin_google_sign_in_disabled`** after status checks and before avatar/login mutation.
The account re-fetch after a failed insert applies the same status and role gates.

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
| **BR-18** | Administrator must use email/password. Google identity resolving to Administrator is rejected with **403 `auth.admin_google_sign_in_disabled`**, after status gates and before mutation/session issuance, including concurrency re-fetch. Verify-email must not issue an Administrator session from a Google provider token. Supersedes v2.0 D3-A. |

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
| 12 | `auth.admin_google_sign_in_disabled` | 403 | google + verify-email Google token guard | Administrator Google method disabled | "Administrator accounts must sign in with email and password." | **v2.1** |

Error codes live in `AuthErrorCodes.cs` as **code constants with technical messages** (no
`dbo.Messages` change). Human-readable text is localized **client-side** (FE `authErrorMapper`,
Mobile `_messageForCode`) — both mappers must add codes #6 and #10. `ApiControllerBase.HandleFailure`
must gain a 503 branch and the pending-approval mapping (§12-#9).

Revision 2.1 additionally maps code #12 to 403 and a Web message directing administrators
to email/password. The Mobile UI update is outside this revision's scope.

---

## 8. Status Matrix (final, ratified)

Google allowances below apply to non-Administrator roles. For Administrator, existing status
failures retain precedence; a passing status is followed by the BR-18 method rejection.

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
| Existing Administrator via Google → 403 method-disabled code; no mutation/session (normal + re-fetch); email/password still succeeds | BR-18 v2.1 |
| Administrator Google token via verify-email → 403; pending email status not activated; no session | BR-18 v2.1 |

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
| Registration + email verification + resend (except v2.1 Administrator Google session guard) | UC-01 |
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
| BR-18 | Approved v2.1 (supersedes D3-A) | §5 + revision 2.1 | Google normal/re-fetch + verify-email + HTTP rejection; password regression | T12–T13 |

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

*End of specification — UC-04 Sign In, v2.1 (C1–C5 baseline + approved revision, 2026-09-14).*

## Revision 3 — Web session and account/application separation (design consolidated 2026-09-14)

Status (updated 2026-09-15): core W01/T16 contract APPROVED/DONE; runtime implementation DONE for T17-T22 (Web sign-in/Admin/Google, verify-email, core acceptance, and Web refresh/session restoration); T23 sign-out/revocation intentionally deferred to the separate Sign Out UC. UC-04 SIGN IN: DONE (see "Final delivery status" at the end of this file). This section overrides conflicting v2.0/v2.1 target rules for Web sessions and application eligibility; historical sections below/above remain baseline history, not acceptance for the new revision. The v2.x "no refresh/logout endpoint" limitation (N5) and the T17-T20 "refresh pending" progress notes are historical snapshots superseded by T22. BR-13 remains retired; Administrator Google prohibition remains mandatory. Canonical Web detail: [UC-04 Web Revision 3](../../Capstone_FE/specs/UC-04-web-spec.md). Plan: [BE revision 3 tasks](../plans/UC-04-plan.md).

### R3-01 — Explicit Web contracts and compatibility

Introduce Web verification-only POST /api/v1/auth/web/verify-email, Firebase Bearer, no body. Validate fresh Firebase evidence/verified email, find existing account by verified token email, apply restrictions, synchronize only. Eligible PendingEmailVerification becomes Active; existing verification timestamp/profile/role preserved. No token, refresh row, Set-Cookie or authenticated session. Success verification envelope data emailVerified=true. Missing Bearer401 AUTH_HEADER_MISSING; invalid token401 MSG14; unverified403 MSG_EMAIL_NOT_VERIFIED; missing usable email400 auth.verification_email_missing; missing account400 MSG_USER_NOT_FOUND; Locked/Inactive403 existing codes; identifiable infrastructure503 auth.verification_unavailable; internal/DB500 safe error. New codes/typed operational error mapping require implementation.

Keep existing /auth/verify-email Mobile verification-plus-session response and Administrator Google guard. Do not globally strip credentials or select behavior implicitly by client/header. Web Register link handler uses new endpoint and no longer saves tokens. Web password recovery synchronizes once then repeats password sign-in exactly once; sign-in alone establishes normal Web session/full context.

### R3-02 — Admin-only gate and failed sign-in

Admin UI uses explicit server Admin contract: credentials → account gates → current DB role → only Administrator session. Non-admin403 auth.admin_access_required before any new token/refresh persistence/cookie replacement. Failed B sign-in does not revoke existing A/cookie. Public Web supported roles retain generic entry; Administrator password → admin, Google denied.

### R3-03 - Account/profile separation and shared compatibility

### Legacy compatibility - approved, no DB normalization in Sign In

Only inspect Users.status in {PendingApproval, Rejected}. Authenticate credentials/Google evidence, load account/current role, handle PendingEmailVerification/Locked/Inactive, then run a shared ResolveAccountEligibility(account, profile) BEFORE the old legacy rejection gate.

Require TourOperator role + matching OperatorProfile + valid compatible profile status + authoritative confirmed email verification. Only PendingApproval/PendingApproval and Rejected/Rejected map successfully. Legacy/profile conflict (including PendingApproval/Approved or Rejected/PendingApproval), absent profile, unsupported status, nonoperator role or uncertain verification fails with auth.account_state_unresolved (APPROVED HTTP403), no new session/token/cookie or account activation.

PASS - effective authentication status Active; applicationStatus from profile; continue authentication. This response status is normalized/effective contract status, NOT evidence that Users.status in DB was updated. Do NOT mutate account/profile status or verification data as a side effect of resolving. Normal refresh/login timestamp and refresh-session persistence remain separately governed by their contracts.

Use the same eligibility semantics in password, existing-account Google (including concurrency re-fetch), refresh and relevant protected-API account checks. Unknown-email Google Traveler creation is outside legacy mapping. Inventory raw Users.status checks; distinguish account eligibility from role-specific approval rules, without implementing unrelated business UCs. Partner business remains eligible account + TourOperator + current Approved profile; matching legacy pending/rejected never grants business access.

CASE A legacy account without sufficient evidence - authentication denied/account_state_unresolved. CASE B DB Active, valid TourOperator session, missing/unknown applicationStatus - authenticated application-unresolved state, no business, no cleanup.

Verification evidence audit: Users.EmailVerifiedAtUtc is an existing persisted field. VerifyEmailCommandHandler currently writes it only on PendingEmailVerification activation; its response timestamp fallback is NOT persisted evidence. Implementation must audit timestamp writers/provenance and establish the trusted stored marker used consistently by password/Google/refresh/API checks. Never infer verification from profile existence, legacy status, FE storage or response fallback. Any alternative evidence mechanism needs explicit review; do not silently add Firebase dependency to normal password login or refresh. Missing reliable evidence fails unresolved; no automatic evidence backfill. Live-data audit of suspicious records belongs before migration/release, not a resolver implementation prerequisite; no production data changes authorized.

Keep schema/legacy enum values and old Approve handler unchanged in this core task. Data normalization and approval-lifecycle writer changes are future coordinated owning-UC work, not Sign In acceptance. Future normalization requires compatible code before/with data change.


### R3-04 — Web credentials and refresh

Access 15min, Web memory only, Bearer for APIs. Refresh random token hash in existing RefreshTokens, original7day expiry, no rotation/sliding expiry/new table/column/requestId/cache. Web sign-in returns access/full context, raw refresh cookie only (not Web JSON). Refresh verifies hash/revoked_at/expires_at; current DB account/role/profile; issues access/context without new refresh session/expiry. Manual Partner retry allowed before access expiry and uses same FE single-flight as auto/restore/API retry.

Invalid/expired/revoked refresh → reauthenticate; Locked/Inactive → clear with reason; transport/timeout/5xx/applicable429 → retain recoverable UI state, deny protected use, allow retry/Retry-After. Final exact refresh error catalog requires consolidated contract review.

### R3-05 — Cookie/CORS conditional MVP

Production HTTPS same-site FE/BE required; exact domains unconfirmed. Cookie HttpOnly, Secure production, SameSite=Lax, host-only Domain omitted, Path=/api/v1/auth encompassing Web refresh/sign-out. Keep-me-signed-in off session cookie; on persistent no later than original token expiry. Browser restore caveat: closure not revoke. CORS exact origins+credentials; FE credentials include. POST/approved content types for session side effects. Optional future custom header/Origin middleware, not mandatory MVP. Different-site topology requires cookie/CSRF review before SameSite=None; CORS alone insufficient. Dev exceptions never copied to production.

### R3-06 — Invalid context and sign-out dependency

Malformed success role/access/account status → FE fail closed/no routing/fallback, clear runtime/frontend state and attempt approved Web sign-out. Sign-out sets revoked_at and expires matching-scope cookie; absent/expired/revoked sessions safe retry. Temporary cleanup error stays locally unauthenticated, not claimed revoke. Persistent cleanupPending across reload/multitab cleanup NOT approved mandatory MVP; optional hardening. Valid operator unresolved application is separate and retains session. JWT can stay valid until original expiry after refresh revocation. Normal logout UX belongs UC Sign Out; cleanup API is a dependency.

### Approved core bindings and separate session review items

APPROVED core Web bindings: POST /auth/web/login, /auth/web/admin/login (JSON email/password/keepMeSignedIn), /auth/web/google (idToken/keepMeSignedIn), /auth/web/refresh and /auth/web/sign-out (cookie, no credential body). Core bindings/body names, cookie tripmate_refresh, legacy evidence/effective status and error catalog are APPROVED in the W01 packet. Refresh/sign-out schemas remain separate review work. Existing Mobile bindings unchanged. Web auth success envelope current userId/email/fullName/role/status/accessToken/accessTokenExpiresAtUtc/applicationStatus, Google additionally isNewAccount; NO raw refreshToken. Preserve approved login/Google ProblemDetails conventions; do not globally harmonize verification/Mobile errors.

### Traceability and acceptance status

R3-01 → Web AC05/06/15; R3-02 → AC03/12; R3-03 → AC07/08/16/17; R3-04 → AC09/10/11; R3-05 → AC14; R3-06 → AC13/18. Required handler/API/persistence and real client evidence, plus baseline regression. Historical v2.x test results do not prove R3; new tasks NOT EXECUTED. Approval/reject/resubmit/notifications/business modules are explicitly owned dependencies, no automatic scope expansion.

### R3 scope amendment - latest approved Sign In boundary

Core: public/Admin/Google authentication, account/role/current application context, cookie issuance/access memory/persistence mode, Web sync-only password recovery, fail-closed and role destinations/placeholder. Shared legacy resolver replaces core normalization; existing Approve handler not modified by core. Verify code evidence/protected eligibility inventory is compatibility work; full live DB audit is not a resolver implementation blocker, not entire business UC implementation.

Refresh/restoration is separate session package retained for this delivery. Register link migration and sign-out cleanup are compatibility dependencies. Multi-tab switching, persistent cleanupPending, DB normalization, approval/reject/resubmit/business modules are deferred/owning UCs. Previous R3 normalization/approval sequencing describes future release only, not core tasks. Split acceptance CORE / SESSION PACKAGE / DEPENDENCY; do not claim remembered-session restoration before refresh implemented.

### W01 / T16 - Documentation-only contract gate (DONE)

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


## W01/T16 approved contract - DONE

W01 (FE docs) and T16 (BE docs/read-only audit) are TWO SIDES OF ONE contract gate. Neither waits for implementation of the other. User sign-off is recorded; the documentation prerequisite for W02/T17+ is satisfied; no runtime code, tests or DB mutation is authorized in this phase. This APPROVED packet supersedes earlier candidate/cookie/message placeholders. W01/T16 = DONE; runtime work is not executed or authorized by this documentation update.

### A. Verification evidence solution and code audit

Audited source writers of Users.EmailVerifiedAtUtc:
1. VerifyEmailCommandHandler: verified Firebase ID token + EmailVerified=true + existing eligible PendingEmailVerification account -> server UTC timestamp persisted on activation.
2. GoogleAuthCommandHandler: validated google.com provider, verified-email claim and usable email -> timestamp on NEW Traveler creation only. It does not supply missing persisted evidence for an existing legacy TourOperator.
RegisterTraveler initializes PendingEmailVerification, no verified timestamp. EF maps the existing property to Users.email_verified_at. No client-writable verification timestamp or database seed assignment was found in inspected source/database scripts. Tests/fake values are not production provenance.

APPROVED MVP evidence mechanism: use the PERSISTED Users.EmailVerifiedAtUtc read from DB, under the same trusted-server-controlled data assumption as account/role data. Require a non-null UTC timestamp with CreatedAtUtc <= EmailVerifiedAtUtc <= BE current UTC. No profile/status/FE/token-response inference. A future-dated or pre-creation timestamp is unresolved, not automatically repaired. DB is authoritative for MVP; a valid persisted marker is sufficient. Suspicious/imported/manual records need operational audit before migration/release, not whole-DB audit before implementing resolver. Actual DB contents were not queried or changed.

VerifyEmailResponse fallback (user.EmailVerifiedAtUtc ?? now) is computed response data, NOT persisted evidence. NULL stays insufficient even if a prior response reported a timestamp or current Google identity is verified. Legacy resolver never writes a marker, activates DB account or calls Firebase to patch missing evidence. Ordinary password/refresh/API eligibility remain independent of new Firebase calls. A different evidence/backfill mechanism requires a separate reviewed decision.

Resolver order/matrix remains as approved: validate credential/Google evidence; load current account/role; handle PendingEmailVerification/Locked/Inactive; before legacy rejection resolve exact PendingApproval/PendingApproval or Rejected/Rejected with matching profile/role and evidence above. PASS -> effective response status Active and current profile applicationStatus, no DB status update. FAIL ->403 auth.account_state_unresolved, no session/new cookie. Same semantics across password/existing Google including re-fetch/refresh/relevant API eligibility. DB Active + unresolved application remains valid session with business denied.

### B. Approved endpoint/request/success schema

POST /api/v1/auth/web/login and /api/v1/auth/web/admin/login accept application/json:
{"email":"user@example.com","password":"...","keepMeSignedIn":false}
email/password required non-null strings; trim email before validation, BE lowercase lookup; password nonempty and untouched. keepMeSignedIn optional boolean default false; null/wrong type invalid. Reject malformed JSON and unsupported content type. No role/status supplied by client authorizes access; unknown extra members follow existing JSON skip behavior.

POST /api/v1/auth/web/google accepts application/json {"idToken":"...","keepMeSignedIn":false}; ID token body-only, not altered; missing/empty -> AUTH_TOKEN_MISSING; default boolean rule same as above.

POST /api/v1/auth/web/verify-email takes Firebase Bearer, no body; it neither requires nor establishes TripMate session. Refresh/sign-out remain separately reviewed session/dependency endpoints, not core implementation.

Sign-in HTTP200 ApiResponse envelope data:
{"userId":123,"email":"operator@example.com","fullName":"Operator","role":"TourOperator","status":"Active","applicationStatus":"PendingApproval","accessToken":"<access token>","accessTokenExpiresAtUtc":"2026-09-14T10:15:00Z"}

userId: positive integer JSON number (existing BE long; current Web must handle only safely representable IDs, a future large-ID transport contract is separate).
email: normalized non-null string; fullName: non-null string (existing entity default empty permitted, UI may display email).
role: Traveler/TourOperator/Administrator string enum.
status: effective authentication status, success Active; not proof of persisted Active for legacy accounts.
applicationStatus: REQUIRED string | null. Traveler/Administrator null; TourOperator Approved/PendingApproval/Rejected from recognized profile, otherwise null and application-unresolved UI. Never omit or assume approved.
accessToken: nonempty string; accessTokenExpiresAtUtc: UTC ISO8601 string. Google adds isNewAccount:boolean. No refreshToken field in any Web response.
Verification success same envelope data {"emailVerified":true}; no auth context/session.

### C. Approved cookie name and attributes

Name = tripmate_refresh (FINAL/APPROVED).
HttpOnly=true; Secure=true production; SameSite=Lax; Domain omitted; Path=/api/v1/auth.
Keep me signed in OFF: session cookie, omit persistent Max-Age/Expires.
ON: persistent cookie expiry at original refresh expiry, maximum seven days after sign-in.
No rotation/sliding expiry. Sign-out deletes same name/path/domain scope. FE credentials: include; no raw refresh token storage/access.
Production HTTPS SAME SITE required, exact FE-origin CORS + credentials. Dev localhost may use Secure=false for local HTTP only, no dev origins/exception in production. Different-site deployment requires review before SameSite=None.
Access15min memory-only. Cookie hardening/deployment limitations remain approved MVP scope.

### D. Approved error/message/action catalog (core Web)

Exact copy below is English to match existing Web auth UI/mapper language, except previously approved Vietnamese Admin/legacy/context messages. Scope keys by endpoint/field where the same code has different meanings. Do not blindly reuse MSG14 verification-link copy for a Firebase ID token rejection. Tables describe target Web contract, not currently implemented error mappings.

| Applies / trigger | HTTP | Code or structured discriminator | Exact FE message | Action |
| --- | --- | --- | --- | --- |
| Password/Admin missing email | 400 | field email / MSG01 | Please enter your email. | Focus email, preserve other values |
| Password/Admin malformed email | 400 | field email / MSG02 | Invalid email format. Please enter a valid email address. | Focus email |
| Password/Admin missing password | 400 | field password / MSG01 | Please enter your password. | Focus password, no strength rule |
| Web invalid JSON/boolean/model binding | 400 | auth.request_invalid (new Web-specific APPROVED code) | Unable to submit sign-in. Please check your details and try again. | Keep form, correct request, no auto retry |
| Web wrong Content-Type | 415 | HTTP415 fallback if no code | Unable to submit sign-in. Please try again. | Contract error, no automatic retry |
| Password/Admin unknown email/wrong password/no hash | 401 | auth.invalid_credentials | Invalid email or password. Please try again. | Keep form, allow editing, no refresh interceptor |
| Password/Admin unverified account | 403 | MSG_UNVERIFIED | Please verify your email before signing in. | Recovery at most once; if not Firebase verified show resend |
| Existing Google unverified TripMate account | 403 | MSG_UNVERIFIED | Please verify your TripMate email before signing in. | Direct password/verification flow, no Google auto recovery |
| Any account Locked | 403 | auth.account_locked | Your account is locked. Please contact support. | Stop auth, support; failed login preserves existing session |
| Any account Inactive/unsupported restricted state | 403 | auth.account_inactive | Your account is inactive. Please contact support. | Stop auth, support |
| Admin entry non-admin eligible account | 403 | auth.admin_access_required (new) | Tài khoản này không có quyền truy cập khu vực quản trị. Vui lòng đăng nhập tại trang dành cho người dùng. | Public sign-in link; no new session/cookie |
| Google Administrator | 403 | auth.admin_google_sign_in_disabled | Administrator accounts must sign in with email and password. | Password sign-in link, no auto email fill |
| Legacy insufficient/conflicting eligibility | 403 | auth.account_state_unresolved (new) | Chưa thể xác định trạng thái tài khoản. Vui lòng liên hệ hỗ trợ. | No session; support, no activation |
| Google missing/empty body token | 400 | AUTH_TOKEN_MISSING | Unable to complete Google sign-in. Please try again. | User may retry popup explicitly |
| Google invalid token/wrong provider/missing provider/email | 401 | AUTH_TOKEN_INVALID | Your Google authentication could not be verified. Please try again. | Explicit new Google attempt, no raw SDK text |
| Google Firebase email false/missing | 403 | MSG_EMAIL_NOT_VERIFIED | Your Google account email could not be confirmed as verified. Please verify it with Google and try again. | No TripMate resend/sync |
| Google Firebase infrastructure unavailable | 503 | auth.firebase_unavailable | Google authentication service is temporarily unavailable. Please try again later. | Preserve form, explicit retry |
| Web verify missing Bearer | 401 | AUTH_HEADER_MISSING | Unable to confirm email verification. Please sign in and try again. | Reauthenticate Firebase, no TripMate session |
| Web verify invalid/expired Firebase token | 401 | MSG14 | Your verification session is invalid or has expired. Please sign in and try again. | Fresh evidence via Firebase; no link-invalid inference |
| Web verify Firebase not verified | 403 | MSG_EMAIL_NOT_VERIFIED | Please verify your email before signing in. | Password verification instructions/resend, stop recovery |
| Web verify missing usable token email | 400 | auth.verification_email_missing (new) | Unable to identify the email being verified. Please sign in and try again. | No sync/session, explicit retry/support |
| Web verify account missing | 400 | MSG_USER_NOT_FOUND | Account not found. Please register first. | Registration link, no auto account creation |
| Web verify Firebase infra unavailable | 503 | auth.verification_unavailable (new) | Email verification is temporarily unavailable. Please try again later. | Preserve state, stop current recovery; explicit retry |
| Any applicable rate limit | 429 | HTTP429 / Retry-After fallback | Too many attempts. Please wait before trying again. | Honor Retry-After; no session invalidation |
| Internal/DB error with legacy generic code | 500 | MSG127 if emitted; otherwise HTTP500 fallback | Something went wrong. Please try again later. | Preserve form; no automatic sign-in retry |
| Service failure without known code | 502/503/504 | HTTP5xx operational fallback | TripMate service is temporarily unavailable. Please try again later. | Preserve form/state, explicit retry |
| Network/CORS transport failure | No HTTP | NETWORK_ERROR client discriminator | Unable to connect to TripMate. Please check your connection and try again. | Preserve form/state, retry; do not guess CORS cause |
| Client request timeout | No HTTP | REQUEST_TIMEOUT | Request timed out. Please try again. | Preserve form, no blind login resubmit |
| Unexpected successful auth response | 200 malformed | INVALID_AUTH_CONTEXT client discriminator | Chưa thể hoàn tất đăng nhập. Vui lòng thử lại. | Fail closed, discard access, attempt sign-out dependency |
| Unknown code/nonconforming response | Any unexpected | UNKNOWN_ERROR client fallback | Unable to complete sign-in. Please try again later. | No raw text/role/home guess; preserve safe local state |
| Valid TourOperator unresolved application | 200 valid session | APPLICATION_STATE_UNRESOLVED client state | Chưa thể xác định trạng thái hồ sơ Partner. Vui lòng thử lại hoặc liên hệ hỗ trợ. | Keep valid session, deny business; manual refresh dependency |
| Cleanup temporarily fails | Network/5xx | CLEANUP_FAILED client state | Chưa thể hoàn tất việc kết thúc phiên đăng nhập. Vui lòng thử lại. | Remain locally unauthenticated, do not claim revoke; no required persistent marker |

Catalog completeness includes missing code fallbacks because current model-binding/ValidationProblemDetails and generic500 do not guarantee an errorCode. New Web contract may supply auth.request_invalid for Web binding failures without rewriting global/Mobile errors. Field validation needs approved field codes, not raw FluentValidation strings. A server-error fallback is client handling, not a fabricated BE code. Rate-limit infrastructure is not newly mandated by this catalog.

### E. Firebase SDK and resend local cases

| Stable Firebase client code | Exact FE message | Action |
| --- | --- | --- |
| auth/popup-closed-by-user / auth/cancelled-popup-request | No message | Silent cancellation; unlock |
| auth/popup-blocked | Please allow popups for TripMate and try again. | Explicit user retry, no redirect/reopen fallback |
| auth/network-request-failed | Unable to connect to TripMate. Please check your connection and try again. | Preserve form, retry |
| auth/too-many-requests | Too many attempts. Please wait a few minutes before trying again. | Resend cooldown60sec where applicable; no guarantee after expiry |
| auth/invalid-credential / auth/wrong-password / auth/user-not-found during password recovery/resend authentication | Invalid email or password. Please try again. | Keep form, stop current recovery |
| auth/invalid-email | Invalid email format. Please enter a valid email address. | Focus email |
| Firebase session absent/mismatched for resend | Please sign in with the email you want to verify before requesting a new link. | Authenticate exact account; do not send another user's email |
| Unlisted SDK error | Unable to complete sign-in. Please try again later. | Safe fallback, unlock; no exception display |

### F. Response-format audit correction and compatibility

Current AuthController verify-email uses success envelope; most failures use HandleFailure -> ProblemDetails/errorCode; missing Bearer uses explicit Error envelope. Generic validation500/binding do not always have code. Earlier wording that all verification failures were envelopes was inaccurate.

APPROVED NEW Web verify-only contract: success envelope, all failures ProblemDetails/errorCode where explicitly defined above (framework/operational fallback as catalog). This format clarification is APPROVED; do not globally change legacy Mobile missing-header/failure consumers. Existing /auth/login, /auth/google and /auth/verify-email Mobile token fields and semantics remain; no token removal or client-header dispatch. Missing-role Mobile bug remains separate.

### G. Gate outcome / why these changes

Code provenance audit completed for inspected writers; live imported/manual-data integrity not proven. Persisted marker rule and required applicationStatus shape are now approved. Cookie tripmate_refresh, core endpoints/DTO, HTTP403 account_state_unresolved, error/message/action catalog and Web verify-only success/failure format are FINAL/APPROVED. W01/T16 = DONE. Suspicious live-data audit is release work, not an implementation blocker. No code/test/DB changes in this gate.

Using existing server-controlled timestamp avoids new schema/Firebase calls; missing data fails closed instead of guessing. Explicit cookie name prevents W02 invention. Endpoint/field-scoped copy avoids MSG14/Google verification confusion. Joint gate avoids circular FE/BE dependency and splits session/owning-UC work from core.
## Approved W01/T16 clarification - applicationStatus and evidence

applicationStatus is REQUIRED in EVERY successful Web sign-in and refresh authentication-context response, type string | null; never omitted. Traveler and Administrator -> null. TourOperator with recognized profile -> Approved/PendingApproval/Rejected exactly; missing/unresolved/unsupported profile -> null. BE must serialize this field even if global JSON configuration normally omits nulls. FE validates key presence and type; null for a valid TourOperator triggers application-unresolved UI, not auth cleanup. A missing field is a contract violation, never treated as approved; retain the previously approved valid-session application-unresolved handling rather than signing out solely for application data. Unknown/malformed application status from a nonconforming response remains fail-closed for business access. Verification-only responses are NOT authentication context and do not add applicationStatus. Existing Mobile contract is not silently changed.

Accepted MVP evidence: DB is authoritative; persisted Users.EmailVerifiedAtUtc with CreatedAtUtc <= timestamp <= BE current UTC is sufficient evidence for legacy resolver. NULL/implausible timestamp -> account_state_unresolved. No additional per-record provenance proof or whole-production-DB audit required to implement the resolver. Audit suspicious/imported/manual legacy data before migration/release of those records; do not turn that operational audit into an implementation gate. No data repair/backfill/migration in resolver.

Plan verification: assert applicationStatus key present for Traveler/Administrator (null), each recognized operator status, unresolved operator (null), sign-in/refresh. Assert valid stored evidence passes, NULL/future/pre-creation fails with no session/status mutation. Add serialization coverage to prevent null omission. W01/T16 evidence mechanism and applicationStatus shape are APPROVED by user; Cookie/error-copy decisions are now APPROVED too; this documentation update does not authorize or execute runtime implementation.


## User sign-off record - 2026-09-14

APPROVED: core Web endpoints/request/DTO contract; cookie tripmate_refresh; HTTP403 auth.account_state_unresolved; current error/message/action catalog including Web auth.request_invalid; Web verify-only success ApiResponse and defined failures ProblemDetails/errorCode; fullName non-null string, empty allowed; applicationStatus always present string | null; DB-authoritative valid verification timestamp evidence. W01/T16 = DONE (docs/audit contract gate only). No implementation/test/migration execution by this update. Separate refresh/sign-out schema and deferred owning-UC tasks are not declared DONE. No commit/push/new branch.


## Revision 3 implementation progress - T17 (2026-09-14; historical snapshot)

Shared legacy account eligibility is implemented in password and existing-account Google sign-in, including concurrency re-fetch. Valid matching legacy operators receive effective Active without stored account/profile status normalization; unresolved legacy eligibility returns HTTP403 auth.account_state_unresolved before a session is persisted. Stored verification timestamp is the approved evidence source. Existing Mobile verification/session response contract remains intact. Local verification: 274 passed, 8 SQL Server checks skipped; build has zero warnings/errors. This records T17 only: applicationStatus response/serialization and full Web endpoint contracts remain T18+ work. See the plan execution evidence for scope and limitations. No commit/push/migration performed.


## Revision 3 implementation progress - T18 (2026-09-14; historical snapshot)

CurrentAccountContext now separates effective account eligibility from current operator application status. Password/Google handlers (including Google concurrency re-fetch) populate recognized application values or null for unresolved Active operators/nonoperators. Application state is internal on existing Mobile DTOs and explicitly serialized on separate Web DTOs, always present including null; Web DTOs do not contain refreshToken. Existing Mobile wire response and verification/session flow are preserved. T18 local suite: 292 passed, 8 SQL Server checks skipped. This is context/DTO implementation only: actual Web endpoint/cookie integration is T20, refresh integration is T22; full Web sign-in acceptance remains pending. No commit/push/schema or lifecycle changes.


## Revision 3 implementation progress - T19 (2026-09-14; historical snapshot)

POST /api/v1/auth/web/verify-email is implemented as verification synchronization only: Firebase Bearer evidence, verified flag/usable email/account restrictions, existing pending-verification activation, ApiResponse data {emailVerified:true}. It issues no credentials/session/refresh-cookie and leaves legacy application/account statuses intact. Defined Web failures use approved ProblemDetails/errorCode, with FirebaseUnavailable distinguished as 503 auth.verification_unavailable. Legacy Mobile verification-plus-session behavior remains unchanged. Local HTTP coverage includes 17 new cases; full BE suite 309 passed/8 SQL Server checks skipped, build zero errors/warnings. See T19 plan evidence and local TestResults/UC04-T19 logs/TRX. Live Firebase/SQL and FE end-to-end integration remain unverified. No commit/push/schema/lifecycle changes.


## Revision 3 implementation progress - T20 (2026-09-14; historical snapshot)

Approved Web password/Admin/Google sign-in endpoints are implemented using current effective account/application context and separate Web DTOs. Admin role gate executes server-side before new session/token persistence; nonadmins receive403 auth.admin_access_required. Successful Web sign-in sets tripmate_refresh with approved HttpOnly/Secure/SameSite=Lax/path/session-or-original-expiry attributes, excluding refreshToken from JSON. CORS configured exact origins now supports credentials. Field validation uses approved MSG01/MSG02 discriminators; binding failures get Web-only auth.request_invalid. Existing Mobile/verification contracts remain supported. 29 new HTTP tests; final BE regression338 passed/8 SQL Server tests skipped; build zero warnings/errors. Evidence in TestResults/UC04-T20 and plan. FE/browser, live Firebase/SQL and T22/T23 restoration/revocation remain pending. No commit/push/schema/deployment changes.


## Final delivery status - 2026-09-15

This section is the current authoritative delivery status and supersedes the "no refresh endpoint"/"refresh pending"/"T22 pending" statements that remain only inside the clearly labeled historical sections above (their test counts are preserved as historical evidence, not rewritten).

Implemented and verified for this delivery:

- W03 Password Web Sign-in, W04 Google Web Sign-in, W05 Email verification recovery, W06 Partner routing: DONE.
- T21 core acceptance: DONE.
- T22 / S01 Web session restoration: DONE — POST /api/v1/auth/web/refresh exists and redeems the HttpOnly tripmate_refresh cookie via the shared AccountEligibilityResolver (SHA-256 hash match; unknown/revoked/expired rejected; current account/role/profile and current OperatorProfiles applicationStatus resolved server-side; fresh short-lived access token issued). Contract unchanged: refresh is non-rotating with the original fixed seven-day expiry (no sliding), no new session row or Set-Cookie on restore, and no raw refreshToken field in any Web JSON response. FE root-layout WebSessionProvider drives the single-flight cold-start restore before role/status-sensitive UI decides Guest; manual browser F5 verification passed.
- CR-11 direct-route authorization for the Partner Web routes: DONE (client navigation protection; future protected Partner action endpoints must still enforce server-side authorization).
- T23 normal Sign Out / revoke-on-signout: OUT OF SCOPE — intentionally deferred to the separate Sign Out UC (D02); no logout/revoke endpoint or cookie-deletion flow was implemented, consistent with the preserved contract.
- UC-02 registration backend and real resubmit backend remain separate UCs.

Final quality gates: dotnet format --verify-no-changes exit 0; Release build 0 warnings/0 errors; full BE suite 372 passed/0 failed/0 skipped with the SQL Server test connection configured (364 passed/8 environment-gated skips when unset); FE 237 passed/0 failed, lint/typecheck/build exit 0; git diff --check clean.

**UC-04 SIGN IN: DONE.** Delivery (commit/push/PR) belongs to the developer and was intentionally deferred until final validation and explicit approval.
