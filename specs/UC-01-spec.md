# UC-01 — Register Traveler Account & Email Verification
## Consolidated Specification (Backend · Web · Mobile)

> **Status: Canonical (Rev. 3 — Aligned with Firebase Authentication & Database Schema `database/tripmate_schema_v7.sql`).**
> This document reconciles all prior drafts and is aligned with SRS (Report 3, §3.2.1) and the TripMate technology stack. Primary Identity Provider is **Firebase Authentication**. Email verification is performed via Firebase email action links (`applyActionCode`). Backend verifies Firebase ID tokens using the Firebase Admin SDK, manages the Traveler profile, and issues TripMate JWT session tokens upon verification.

---

## 1. Overview

A Guest creates a Traveler account by submitting full name, email, password (+ optional phone), and accepting Terms of Service. The frontend registers the user with Firebase Authentication, triggers an email verification link, and registers the Traveler in the TripMate backend with a verified Firebase ID token. The account in `dbo.Users` is created in **PendingEmailVerification** status with `firebase_uid`. The user is presented with a clear **Email Link Verification Instruction Screen** (`/verify-account`) with a 60-second resend cooldown. Once the Traveler clicks the email verification link (`/verify-email?mode=verifyEmail&oobCode=...`), Firebase verifies the action code, and the backend activates the account to **Active** and issues TripMate JWT session tokens upon sign in. A Guest may alternatively register/sign in via **Continue with Google**, which creates or activates an account immediately.

---

## 2. Actors & Preconditions

- **Actor:** Guest (unauthenticated).
- **Preconditions:** Guest is not signed in; system/database/email service are operational.
- **Postcondition (PC-01):** A successful registration creates exactly one `User` (role Traveler) and exactly one `TravelerProfile`, atomically.

---

## 3. Data Model

### 3.1 `User` (`dbo.Users` in `tripmate_schema_v7.sql`)
| Field | Type | Notes |
|---|---|---|
| `Id` | `BIGINT` (`user_id`) | PK, IDENTITY(1,1) |
| `FullName` | `string(150)` (`full_name`) | required, trimmed |
| `Email` | `string(256)` (`email`) | required, unique index `UX_Users_Email` (case-insensitive) |
| `PhoneNumber` | `string(20)` (`phone_number`) | optional, format `0[0-9]{9}`, unique index `UX_Users_Phone` if provided |
| `PasswordHash` | `string(256)` (`password_hash`) | null if account has no password (Google-only) |
| `Role` | `enum: Traveler, TourOperator, Administrator` (`role`) | system-assigned, always `Traveler` here |
| `Status` | `enum: PendingEmailVerification, Active, Locked, Suspended` (`status`) | starts `PendingEmailVerification` (standard) or `Active` (Google) |
| `GoogleProviderId` | `dbo.AuthProviders` (`provider_user_id`) | Google flow only (stored in `dbo.AuthProviders`) |
| `EmailVerifiedAt` | `DATETIME2` (`email_verified_at`) | set upon OTP verification |
| `CreatedAtUtc` / `UpdatedAtUtc` | `DATETIME2` (`created_at`, `updated_at`) | audit |

### 3.2 `TravelerProfile` (`dbo.TravelerProfiles`)
| Field | Type | Notes |
|---|---|---|
| `user_id` | `BIGINT` | PK, FK → `dbo.Users(user_id)` ON DELETE CASCADE (1:1) |
| `TravelPreferences` | nullable | unset at creation |
| `updated_at` | `DATETIME2` | audit |

### 3.3 `EmailVerificationCode` (Redis-backed, key `otp:{userId}`)
| Field | Notes |
|---|---|
| `CodeHash` | SHA-256 (or stronger) hash of the 6-digit code |
| `ExpiresAt` | TTL 10 minutes |
| `AttemptCount` | max 5 before forced invalidation |
| `LastSentAt` | used to enforce 60s resend cooldown |

> Redis is used (not plain in-memory cache) because it's already part of the TripMate stack and works correctly across multiple backend instances. `userId` is the identifier for the OTP record, not email. The `/verify-email` and `/resend-otp` requests accept email for client convenience, but the backend always resolves email $\rightarrow$ User $\rightarrow$ `userId` first, then reads/writes `otp:{userId}` — email is never used directly as the cache key.

---

## 4. Business Rules

- **BR-01 (Uniqueness):** Email unique across all accounts, case-insensitive.
- **BR-01b (Phone Uniqueness):** If provided, phone number must also be unique across all accounts. This extends beyond the base SRS wording (which only specifies email uniqueness) — confirmed as a product decision.
- **BR-02 (Password Policy):** Minimum 8 characters; at least 1 uppercase, 1 lowercase, 1 digit, 1 special character.
- **BR-03 (Hashing):** Password always stored hashed (BCrypt/Argon2id). Plaintext never stored, logged, or returned.
- **BR-04 (Role Assignment):** Role is always system-assigned `Traveler`; client cannot supply a role.
- **BR-05 (Access Restriction):** A `PendingEmailVerification` account cannot log in or access any protected Traveler endpoint. A login attempt with correct credentials but unverified email returns 403 `MSG_UNVERIFIED` (distinct from `MSG03`, so the client doesn't misread it as a duplicate-email error).
- **BR-06 (Google Flow — full decision table):**
  - Email not yet registered $\rightarrow$ create a new `User` (Traveler, `Active`) + `TravelerProfile` + `dbo.AuthProviders` record (`provider: "Google"`). Returns `isNewAccount: true`.
  - Email already registered with the same Google provider link in `dbo.AuthProviders` $\rightarrow$ treat as sign-in, issue tokens for the existing account (`isNewAccount: false`).
  - Email already registered on a password-based account (no Google link) $\rightarrow$ reject with `409 MSG03`; do not silently merge accounts.
  - Google ID token invalid or fails server-side verification $\rightarrow$ `401 MSG_GOOGLE_TOKEN_INVALID`.
  - Google Authentication Service unreachable/timeout $\rightarrow$ `502/503 MSG127`.
- **BR-07 (Atomicity):** `User` + `TravelerProfile` created in a single transaction; both succeed or both roll back.
- **BR-08 (OTP Lifecycle):** 6-digit numeric OTP, hashed at rest, 10-minute TTL, max 5 failed attempts before requiring resend, 60-second cooldown between resend requests. On resend: the previous OTP is invalidated the instant the new one is generated, `AttemptCount` resets to 0, TTL resets to 10 minutes, and `LastSentAt` updates. Resend is rejected if the account is already `Active`.
- **BR-09 (Delivery Failure Tolerance):** If Resend fails or times out — on initial registration or on resend — the newly generated OTP is still saved to Redis (so a subsequent successful send, or a later resend, works against it); the account/OTP state is not rolled back due to email delivery failure. The response indicates `emailSent: false` so the client can offer "Resend Code."
- **Terms Acceptance:** Registration is rejected (client- and server-side) if Terms of Service / Privacy Policy is not accepted.

---

## 5. Validation Rules & Message Mapping

| Condition | HTTP | Code | Message |
|---|---|---|---|
| Required field empty | 400 | `MSG01` | This field is required. |
| Invalid email format | 400 | `MSG02` | Invalid email format. Please enter a valid email address (e.g., user@example.com). |
| Email already registered | 409 | `MSG03` | An account with this email already exists. Please sign in or use another email. |
| Invalid phone format | 400 | `MSG04` | Invalid phone number. Phone number must be 10 digits starting with 0. |
| Phone already registered | 409 | `MSG_PHONE_DUP` | This phone number is already registered to another account. |
| Password fails policy | 400 | `MSG05` | Password must be at least 8 characters, containing uppercase, lowercase, number, and special character. |
| Confirm password mismatch | 400 | `MSG06` | Passwords do not match. Please re-enter. |
| Terms not accepted | 400 | `MSG_TOS` | You must accept the Terms of Service and Privacy Policy to continue. |
| Registration success | 201 | `MSG07` | Account registered successfully! Please check your email for the verification code. |
| OTP incorrect/expired | 400 | `MSG14` | Invalid or expired verification code. Please request a new OTP. |
| Resend cooldown active | 429 | `MSG_COOLDOWN` | Please wait before requesting another code. |
| Generic/server failure | 500 | `MSG127` | Something went wrong. Please try again later. |
| Google account already linked (same provider) | 200 | — | Sign the existing Google-linked account in directly (`isNewAccount: false`) |
| Google email exists on a password account | 409 | `MSG03` | An account with this email already exists. Please sign in or use another email. |
| Google ID token invalid/unverifiable | 401 | `MSG_GOOGLE_TOKEN_INVALID` | Invalid Google authentication token. |
| Google service unavailable | 502/503 | `MSG127` | Something went wrong. Please try again later. |
| Email registered but not verified, login attempted | 403 | `MSG_UNVERIFIED` | Please verify your email before signing in. |
| Account created but verification email failed to send | 201 | `MSG_EMAIL_SEND_FAILED` | Your account was created, but we could not send the verification email. Please try again. |
| OTP attempts exceeded (5 failed tries) | 400 | `MSG_OTP_ATTEMPTS_EXCEEDED` | Too many incorrect attempts. Please request a new verification code. |
| OTP resend success | 200 | `MSG_RESEND_SUCCESS` | A new verification code has been sent. |

> These codes are the single source of truth for BE, Web, and Mobile. All three must reference this table rather than inventing their own strings, so error copy stays consistent across platforms.

---

## 6. API Contract (Backend)

### 6.1 `POST /api/v1/auth/register`

**Request**
```json
{
  "fullName": "Nguyen Van A",
  "email": "traveler@example.com",
  "phoneNumber": "0912345678",
  "password": "Password123!",
  "confirmPassword": "Password123!",
  "acceptedTerms": true
}
```

**Success — `201 Created`**
```json
{
  "userId": 1,
  "email": "traveler@example.com",
  "fullName": "Nguyen Van A",
  "role": "Traveler",
  "status": "PendingEmailVerification",
  "emailSent": true,
  "messageCode": "MSG07"
}
```
*If `emailSent` is false, the account still exists (per BR-09); `messageCode` is `MSG_EMAIL_SEND_FAILED` in that case. The OTP itself is never included in this response, in any environment, including development.*

**Errors**
- `400` — field-level errors (`{ "errors": { "email": "MSG02", "password": "MSG05" } }`)
- `409` — `MSG03` (email) or `MSG_PHONE_DUP` (phone)
- `500` — `MSG127`

### 6.2 `POST /api/v1/auth/verify-email`

**Request**
```json
{
  "email": "traveler@example.com",
  "code": "123456"
}
```
*Backend resolves email $\rightarrow$ User $\rightarrow$ `userId` server-side, then validates the code against `otp:{userId}` — email is a lookup convenience, not the cache key.*

**Success — `200 OK`**
```json
{
  "userId": 1,
  "email": "traveler@example.com",
  "status": "Active",
  "emailVerifiedAt": "2026-09-07T11:15:00Z",
  "accessToken": "jwt",
  "refreshToken": "opaque-token",
  "accessTokenExpiresAtUtc": "2026-09-07T11:15:00Z"
}
```
*Verification always completes the session directly (tokens issued in the same call) — see §7.1 step 14 for the resolved client-navigation behavior.*

**Errors**
- `400` — `MSG14` (incorrect/expired code, or account already verified)
- `400` — `MSG_OTP_ATTEMPTS_EXCEEDED` (5th consecutive failed attempt on the current code)

### 6.3 `POST /api/v1/auth/resend-otp`

**Request**
```json
{
  "email": "traveler@example.com"
}
```

**Success — `200 OK`**
```json
{
  "email": "traveler@example.com",
  "emailSent": true,
  "messageCode": "MSG_RESEND_SUCCESS"
}
```
*On success: previous OTP is invalidated, a new one is generated and saved to Redis (`AttemptCount` reset to 0, TTL reset to 10 min, `LastSentAt` updated) before calling Resend. If the Resend call itself fails, the newly generated OTP is still kept (not rolled back) — `emailSent: false` is returned so a later resend or manual retry can still succeed against it.*

**Errors**
- `400` — account not found, or account already `Active`
- `429` — `MSG_COOLDOWN` (60s not yet elapsed since `LastSentAt`)

### 6.4 `POST /api/v1/auth/google`

**Request**
```json
{
  "idToken": "google-id-token-or-access-token"
}
```
*Note: Accepts token either in the JSON body (`idToken`) or via `Authorization: Bearer <token>` header.*

**Validation Mechanism (`GoogleTokenValidator`):**
1. **Primary (JWT ID Token):** Validated server-side using `GoogleJsonWebSignature.ValidateAsync()` checking Google public keys, audience (`Google:ClientId`), issuer, and expiry.
2. **Fallback (OAuth2 Access Token):** If the token is not a JWT (e.g. Flutter Web `google_sign_in` returning OAuth2 Bearer `ya29...`), backend automatically queries Google's standard `https://www.googleapis.com/oauth2/v3/userinfo` endpoint using the Bearer token to extract `email`, `sub`, `name`, and `picture`.

**Success — `200 OK` / `201 Created`**
```json
{
  "userId": 1,
  "status": "Active",
  "accessToken": "jwt",
  "refreshToken": "opaque-token",
  "isNewAccount": true
}
```
*`isNewAccount: false` when the email already had this same Google provider linked (treated as sign-in, per BR-06).*

**Errors**
- `409` — `MSG03` (email exists on a password-based, non-Google account)
- `401` — `MSG_GOOGLE_TOKEN_INVALID` (token invalid or fails verification)
- `502/503` — `MSG127` (Google service unavailable)

### 6.5 Cross-cutting rules
- All four endpoints are anonymous (`[AllowAnonymous]`), CORS restricted to known Web/Mobile origins.
- Rate-limit `register`, `verify-email`, `resend-otp` per IP and per email.
- Never log password, confirmPassword, OTP plaintext, or Google idToken contents.
- The database transaction never spans the Resend call. Sequence: (1) validate request $\rightarrow$ (2) open DB transaction $\rightarrow$ (3) create `User` + `TravelerProfile` $\rightarrow$ (4) commit $\rightarrow$ (5) generate OTP + save to Redis $\rightarrow$ (6) call Resend $\rightarrow$ (7) return response. The transaction is committed before any external HTTP call is made, so a slow/failing email provider never holds a DB transaction open.
- Log the Resend email id returned on send (not the OTP itself) for support/troubleshooting.

---

## 7. End-to-End Flow

### 7.1 Normal Flow (Email/Password)
1. Guest opens Registration screen (Web or Mobile).
2. Guest enters full name, email, password, confirm password (+ optional phone), accepts Terms.
3. Client-side validation passes $\rightarrow$ `POST /auth/register`.
4. Backend validates payload (`MSG01`/`02`/`04`/`05`/`06`/`MSG_TOS` as applicable).
5. Backend checks email uniqueness (BR-01) $\rightarrow$ `409/MSG03` if taken; if phone provided, also checks phone uniqueness (BR-01b) $\rightarrow$ `409/MSG_PHONE_DUP` if taken.
6. Backend hashes password (BR-03).
7. Backend creates `User` (Traveler, `PendingEmailVerification`) + `TravelerProfile` in one transaction, commits it (BR-07) — before any email is sent.
8. Backend generates OTP, hashes it, stores in Redis with 10-min TTL, `LastSentAt` set.
9. Backend calls Resend to email the OTP (outside the DB transaction). Success $\rightarrow$ response `emailSent: true`, `201 + MSG07`. Failure $\rightarrow$ account and OTP are still persisted, response `emailSent: false` / `MSG_EMAIL_SEND_FAILED`, client offers "Resend Code" (BR-09).
10. Client navigates to the Verify Email screen.
11. Guest enters the 6-digit code $\rightarrow$ `POST /auth/verify-email`.
12. Backend resolves email $\rightarrow$ `userId`, validates the code against `otp:{userId}` (not expired, matches hash, attempt count < 5).
13. On success: `Status` $\rightarrow$ `Active`, `EmailVerifiedAt` set, tokens issued in the same response.
14. Client stores the tokens and signs the Guest in directly, navigating to Home — this resolves the earlier inconsistency between the API returning tokens and the flow text saying "route to Sign In." Verification and first sign-in are the same step, matching the behavior of the Google flow.

### 7.2 Alternative Flow (Google Social Login)
1. At step 2, Guest taps **Continue with Google** instead of filling the form.
2. Client obtains a Google ID token via the platform SDK (web JS SDK / mobile `google_sign_in`).
3. Client sends the ID token to `POST /auth/google`.
4. Backend verifies the token server-side against Google (invalid/unverifiable $\rightarrow$ `401 MSG_GOOGLE_TOKEN_INVALID`; Google unreachable $\rightarrow$ `502/503 MSG127`).
5. Backend applies the BR-06 decision table against the token's email:
   - Not registered $\rightarrow$ create `User` (Traveler, `Active`) + `TravelerProfile` + `dbo.AuthProviders` record — atomic; `isNewAccount: true`.
   - Already registered with the same Google provider link $\rightarrow$ sign in the existing account; `isNewAccount: false`.
   - Already registered as a password-based account $\rightarrow$ reject, `409 MSG03`.
6. Backend issues tokens directly (no OTP) for the create/sign-in outcomes.
7. Client stores tokens, signs the Guest in immediately, navigates to Home.

### 7.3 Resend Flow
1. Guest doesn't receive the code, or it expired, and taps **Resend Code**.
2. Client calls `POST /auth/resend-otp`.
3. Backend checks the account isn't already `Active` (rejects if so) and that 60s have elapsed since `LastSentAt` $\rightarrow$ `429 MSG_COOLDOWN` if too soon.
4. Backend invalidates the previous OTP, generates a new one, resets `AttemptCount` to 0 and TTL to 10 minutes, updates `LastSentAt`, and saves it to Redis.
5. Backend calls Resend to send the new code. Success $\rightarrow$ `emailSent: true`. Failure $\rightarrow$ `emailSent: false`, but the new OTP remains valid in Redis for a subsequent successful send or manual retry.
6. Client restarts the resend cooldown timer on success.

### 7.4 Abnormal Cases
| Case | Behavior |
|---|---|
| Required field empty | `MSG01`, no account created |
| Invalid email format | `MSG02` |
| Password fails policy | `MSG05` |
| Confirm password mismatch | `MSG06` |
| Terms not accepted | `MSG_TOS`, submission blocked client-side and rejected server-side |
| Email already registered | `MSG03`, no account created |
| Phone already registered | `MSG_PHONE_DUP`, no account created |
| Persistence failure | `MSG127`, transaction rolled back — no User, no TravelerProfile |
| Resend/email delivery failure | Account and OTP stay valid, `emailSent: false` / `MSG_EMAIL_SEND_FAILED`, resend available |
| OTP incorrect/expired | `MSG14`, account stays `PendingEmailVerification`, `AttemptCount` incremented |
| 5 failed OTP attempts | `MSG_OTP_ATTEMPTS_EXCEEDED`, OTP invalidated, Guest must request a new one |
| Resend requested before cooldown elapses | `429 MSG_COOLDOWN` |
| Resend requested for an already-Active account | 400, rejected |
| Guest never verifies | Account stays `PendingEmailVerification` indefinitely; login attempt returns `403 MSG_UNVERIFIED` (BR-05) |
| Google auth unavailable/timeout | `502/503 MSG127`, no account created |
| Google ID token invalid/unverifiable | `401 MSG_GOOGLE_TOKEN_INVALID`, no account created |
| Google email exists on a password account | `409 MSG03`, no account created, no auto-merge |
| Google email exists with same Google link | Treated as sign-in, tokens issued, no new account created |
| Invalid phone format (if provided) | `MSG04` |
| Login attempt while PendingEmailVerification | `403 MSG_UNVERIFIED` |

---

## 8. Backend Implementation Notes (Clean Architecture)

- **Domain:** `User`, `TravelerProfile` entities; `UserRole`/`AccountStatus` enums; a factory (e.g. `User.CreateTraveler(...)`) enforcing role/status defaults so callers can't bypass BR-04.
- **Application:** `RegisterTravelerCommand`, `VerifyEmailCommand`, `GoogleAuthCommand` (+ handlers); FluentValidation validators; abstraction `IFirebaseAuthService` (validates Firebase ID token and extracts verified uid, email, and emailVerified claim).
- **Infrastructure:** EF Core mapping for `User` (`firebase_uid`, `email`, `status`); `FirebaseAuthService` using Firebase Admin SDK to verify RS256 Firebase ID tokens; Google token validator for Google Sign-In.
- **API:** `AuthController` exposing `/register`, `/verify-email`, `/google`, `/login`; bearer token extracted from `Authorization: Bearer <idToken>`.
- **Reliability:** Account rollback compensation in frontend if backend registration fails after Firebase user creation; multi-device verification safety by requiring Firebase sign-in after link verification.

---

## 9. Web Implementation (Next.js)

### 9.1 Screens
- `/register` — Registration form.
- `/verify-account?email={encoded}` — OTP entry, pre-filled email from query string.
- Redirect target after verification: Home, signed in directly (see §7.1 step 14 — verification and first sign-in are the same step).

### 9.2 Form fields (Register)
Full Name, Email, Password (with visibility toggle), Confirm Password, Phone (optional, masked), Terms & Privacy checkbox, buttons: **Register**, **Continue with Google**, **Back to Sign In**.

### 9.3 Client-side validation (mirrors §5, never replaces server validation)
Required fields $\rightarrow$ `MSG01`; email regex $\rightarrow$ `MSG02`; live password-policy checklist $\rightarrow$ `MSG05`; confirm-match on blur/submit $\rightarrow$ `MSG06`; phone regex `^0\d{9}$` if filled $\rightarrow$ `MSG04`; submit disabled until Terms checked.

### 9.4 Data flow
- React Hook Form + Zod schema mirroring backend rules.
- API calls to `/auth/register`, `/auth/verify-email`, `/auth/resend-otp`, `/auth/google`.
- Google login via Google Identity Services JS SDK $\rightarrow$ ID token $\rightarrow$ `POST /auth/google`.
- On success: store tokens, redirect to Home.

---

## 10. Mobile Implementation (Flutter/Bloc)

### 10.1 Screens
- `RegisterScreen` (route `AppRoutes.register`).
- `VerifyEmailScreen` (route `AppRoutes.verifyEmail`, receives `email` param).

### 10.2 Bloc structure
- `RegisterBloc`: events `RegisterSubmitted`, `GoogleRegisterRequested`.
- `VerifyEmailBloc`: events `CodeSubmitted`, `ResendRequested`.

### 10.3 Test checklist & Acceptance Criteria
- Run `flutter analyze`, `flutter test`, and `flutter test integration_test`.
- AC-M1 to AC-M6 verified against §5 validation table and §7.1 - 7.3 flows.

---

## 11. Detailed Form Validation Matrix & UAT Test Suite

### 11.1 Field Validation Rules (Web & Mobile)

#### 1. Full Name
- **Required / Empty / Whitespace only:** Fail $\rightarrow$ `Vui lòng nhập họ và tên.`
- **Contains Digits (e.g. `Nguyen Minh Phuc1`):** Fail $\rightarrow$ `Họ và tên không được chứa chữ số.`
- **Contains Special Characters (e.g. `Nguyen@Phuc`):** Fail $\rightarrow$ `Họ và tên chỉ được chứa chữ cái và khoảng trắng.`
- **Vietnamese Unicode (e.g. `Nguyễn Minh Phúc`):** Pass
- **Length < 2:** Fail $\rightarrow$ `Họ và tên phải có ít nhất 2 ký tự.`
- **Length > 100:** Fail $\rightarrow$ `Họ và tên không được vượt quá 100 ký tự.`
- **Multiple spaces (e.g. `Nguyen   Minh`):** Normalized to `Nguyen Minh`.

#### 2. Email Address
- **Required / Empty:** Fail $\rightarrow$ `Vui lòng nhập email.`
- **Invalid format / Length > 254:** Fail $\rightarrow$ `Vui lòng nhập địa chỉ email hợp lệ.`
- **Leading/trailing spaces:** Trimmed automatically (`phuc@gmail.com`).
- **Already Registered (API response):** Fail $\rightarrow$ `Email này đã được đăng ký. Vui lòng đăng nhập hoặc sử dụng email khác.`

#### 3. Phone Number (Optional)
- **Empty:** Pass
- **10 digits starting with 0 (`0905123456`):** Pass
- **Spaces between digits (`0905 123 456`):** Normalized to `0905123456`
- **Invalid format / Length != 10 / Non-digits:** Fail $\rightarrow$ `Số điện thoại không hợp lệ.`
- **Already Used (API response):** Fail $\rightarrow$ `Số điện thoại này đã được đăng ký.`

#### 4. Password
- **Required / Empty:** Fail $\rightarrow$ `Vui lòng nhập mật khẩu.`
- **Leading/trailing spaces:** Fail $\rightarrow$ `Mật khẩu không được bắt đầu hoặc kết thúc bằng khoảng trắng.`
- **Length < 8:** Fail $\rightarrow$ `Mật khẩu phải có ít nhất 8 ký tự.`
- **Length > 128:** Fail $\rightarrow$ `Mật khẩu không được vượt quá 128 ký tự.`
- **Policy Rules (Uppercase, Lowercase, Digit, Special Character):**
  - Missing all required char types $\rightarrow$ `Mật khẩu phải chứa chữ hoa, chữ thường, chữ số và ký tự đặc biệt.`
  - Missing uppercase, digit & special $\rightarrow$ `Mật khẩu phải chứa chữ hoa, chữ số và ký tự đặc biệt.`
  - Missing uppercase & special $\rightarrow$ `Mật khẩu phải chứa chữ hoa và ký tự đặc biệt.`
  - Missing special character only $\rightarrow$ `Mật khẩu phải chứa ít nhất một ký tự đặc biệt.`

#### 5. Confirm Password
- **Required / Empty:** Fail $\rightarrow$ `Vui lòng xác nhận mật khẩu.`
- **Mismatch with Password:** Fail $\rightarrow$ `Mật khẩu xác nhận không khớp.`

#### 6. Terms of Service & Privacy Policy
- **Unchecked Checkbox:** Fail $\rightarrow$ `Vui lòng đồng ý với Điều khoản sử dụng và Chính sách bảo mật.`
- **Modal Link Click:** Opens modal view without error.

---

### 11.2 Acceptance Test Matrix (UAT-REG-01 to UAT-REG-25)

| ID | Test Case | Primary Input | Expected Result |
|---|---|---|---|
| **UAT-REG-01** | Đăng ký hợp lệ | Tất cả trường hợp lệ | Pass |
| **UAT-REG-02** | Full Name bỏ trống | Empty | Fail (`Vui lòng nhập họ và tên.`) |
| **UAT-REG-03** | Full Name có số | `Nguyen Phuc1` | Fail (`Họ và tên không được chứa chữ số.`) |
| **UAT-REG-04** | Full Name có ký tự đặc biệt | `Nguyen@Phuc` | Fail (`Họ và tên chỉ được chứa chữ cái và khoảng trắng.`) |
| **UAT-REG-05** | Full Name có tiếng Việt | `Nguyễn Minh Phúc` | Pass |
| **UAT-REG-06** | Email bỏ trống | Empty | Fail (`Vui lòng nhập email.`) |
| **UAT-REG-07** | Email sai định dạng | `abc@` | Fail (`Vui lòng nhập địa chỉ email hợp lệ.`) |
| **UAT-REG-08** | Email đã tồn tại | Existing email | Fail (`Email này đã được đăng ký...`) |
| **UAT-REG-09** | Phone bỏ trống | Empty | Pass (Optional) |
| **UAT-REG-10** | Phone sai định dạng | `0905abc123` | Fail (`Số điện thoại không hợp lệ.`) |
| **UAT-REG-11** | Phone đã tồn tại | Existing phone | Fail (`Số điện thoại này đã được đăng ký.`) |
| **UAT-REG-12** | Password bỏ trống | Empty | Fail (`Vui lòng nhập mật khẩu.`) |
| **UAT-REG-13** | Password dưới 8 ký tự | `Ab@123` | Fail (`Mật khẩu phải có ít nhất 8 ký tự.`) |
| **UAT-REG-14** | Password thiếu chữ hoa | `password@123` | Fail (`Mật khẩu phải chứa chữ hoa...`) |
| **UAT-REG-15** | Password thiếu chữ thường | `PASSWORD@123` | Fail (`Mật khẩu phải chứa chữ thường...`) |
| **UAT-REG-16** | Password thiếu số | `Password@` | Fail (`Mật khẩu phải chứa chữ số...`) |
| **UAT-REG-17** | Password thiếu ký tự đặc biệt | `Password123` | Fail (`Mật khẩu phải chứa ít nhất một ký tự đặc biệt.`) |
| **UAT-REG-18** | Password hợp lệ | `Password@123` | Pass |
| **UAT-REG-19** | Confirm Password không khớp | Khác Password | Fail (`Mật khẩu xác nhận không khớp.`) |
| **UAT-REG-20** | Confirm Password khớp | Giống Password | Pass |
| **UAT-REG-21** | Chưa tick Terms | Checkbox false | Fail (`Vui lòng đồng ý với Điều khoản...`) |
| **UAT-REG-22** | Double-click Register | Nhấn liên tục | Chỉ gửi 1 request (loading state) |
| **UAT-REG-23** | API trả email tồn tại | `EMAIL_ALREADY_EXISTS` | Hiển thị đúng lỗi trên field Email |
| **UAT-REG-24** | API trả lỗi hệ thống | `SERVER_ERROR` | Hiển thị thông báo lỗi thân thiện |
| **UAT-REG-25** | Đăng ký thành công | HTTP 201 | Chuyển hướng Verify Email |
