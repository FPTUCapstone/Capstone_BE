# UC-01 — Register Traveler Account & Email Verification
## Consolidated Specification (Backend · Web · Mobile)

> **Status: Canonical (Rev. 3 — Aligned with Firebase Authentication & Database Schema `database/tripmate_schema_v7.sql`).**
> This document reconciles all prior drafts and is aligned with SRS (Report 3, §3.2.1) and the TripMate technology stack. Primary Identity Provider is **Firebase Authentication**. Email verification is performed via Firebase email action links (`sendEmailVerification` / `applyActionCode`). Backend verifies Firebase ID tokens using the Firebase Admin SDK (`FirebaseAuthService`), validates account constraints, manages Traveler records, and issues TripMate JWT session tokens upon verification.

---

## 1. Overview

A Guest creates a Traveler account by submitting full name, email, password (+ optional phone), and accepting Terms of Service.
1. **Frontend Registration:** The frontend registers the user with Firebase Authentication (`createUserWithEmailAndPassword`), acquires the Firebase ID token, and calls the TripMate backend `POST /api/v1/auth/register` with `Authorization: Bearer <firebaseIdToken>`.
2. **Backend Account Creation:** The backend verifies the Firebase ID token, checks email and phone uniqueness in `dbo.Users`, hashes the password, and creates the account in **PendingEmailVerification** status. No JWT tokens are issued at this stage.
3. **Verification Email:** The frontend triggers an email verification link via Firebase (`sendEmailVerification`) pointing to `${origin}/verify-email`. The user is navigated to an instruction screen (`/verify-account`).
4. **Email Confirmation & Activation:** When the Traveler clicks the verification link (`/verify-email?mode=verifyEmail&oobCode=...`), Firebase verifies the action code (`applyActionCode`). The frontend reloads the user session, requests a refreshed Firebase ID token (with claim `email_verified: true`), and sends it via `Authorization: Bearer <token>` to `POST /api/v1/auth/verify-email`.
5. **Session Token Issuance:** The backend verifies the updated Firebase ID token, checks `email_verified == true`, marks the account **Active**, stores a hashed refresh token in `dbo.RefreshTokens`, and issues TripMate JWT access & refresh tokens.
6. **Google Social Login:** A Guest may alternatively register/sign in via **Continue with Google** (`signInWithPopup` with `GoogleAuthProvider`), acquiring a Firebase ID token and sending it to `POST /api/v1/auth/google`. The backend validates the token, activates or provisions the account, and issues TripMate JWT session tokens immediately.

---

## 2. Actors & Preconditions

- **Actor:** Guest (unauthenticated).
- **Preconditions:** Guest is not signed in; system, database, and Firebase Authentication services are operational.
- **Postcondition (PC-01):** A successful registration creates exactly one `User` (role Traveler) in `PendingEmailVerification` status, transitioning to `Active` with TripMate JWT tokens upon email verification.

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
| `AvatarUrl` | `string(500)` (`avatar_url`) | profile picture (synced from Google if available) |
| `EmailVerifiedAtUtc` | `DATETIME2` (`email_verified_at`) | set upon successful email verification or Google sign-in |
| `CreatedAtUtc` / `UpdatedAtUtc` | `DATETIME2` (`created_at`, `updated_at`) | audit timestamps |

### 3.2 `RefreshToken` (`dbo.RefreshTokens`)
| Field | Type | Notes |
|---|---|---|
| `Id` | `BIGINT` | PK, IDENTITY(1,1) |
| `UserId` | `BIGINT` | FK → `dbo.Users(user_id)` ON DELETE CASCADE |
| `TokenHash` | `string(256)` | SHA-256 hash of opaque refresh token string |
| `ExpiresAtUtc` | `DATETIME2` | Refresh token expiry (typically 7 days) |
| `CreatedAtUtc` | `DATETIME2` | Creation timestamp |
| `RevokedAtUtc` | `DATETIME2?` | Set if token revoked |

---

## 4. Business Rules

- **BR-01 (Uniqueness):** Email unique across all accounts, case-insensitive.
- **BR-01b (Phone Uniqueness):** If provided, phone number must also be unique across all accounts.
- **BR-02 (Password Policy):** Minimum 8 characters; at least 1 uppercase, 1 lowercase, 1 digit, 1 special character.
- **BR-03 (Hashing):** Password always stored hashed (BCrypt/Argon2id). Plaintext never stored, logged, or returned.
- **BR-04 (Role Assignment):** Role is always system-assigned `Traveler`; client cannot supply a role.
- **BR-05 (Access Restriction):** A `PendingEmailVerification` account cannot log in or access protected endpoints. A login attempt with correct credentials but unverified email returns `403 MSG_UNVERIFIED`.
- **BR-06 (Google Flow Decision Table):**
  - Email not yet registered $\rightarrow$ create a new `User` (Traveler, `Active`), record `EmailVerifiedAtUtc = now`, issue TripMate JWT session tokens (`isNewAccount: true`).
  - Email already registered $\rightarrow$ activate account if pending, update avatar if empty, update `LastLoginAtUtc`, issue TripMate JWT session tokens (`isNewAccount: false`).
  - Google ID token invalid or fails server verification $\rightarrow$ `401 MSG_GOOGLE_TOKEN_INVALID`.
- **BR-07 (Firebase ID Token Enforcement):**
  - Registration requires a valid Firebase ID token in `Authorization: Bearer <token>`. The email claimed in the token must match the registration email.
  - Email verification requires a refreshed Firebase ID token containing `email_verified == true`.
- **BR-08 (Email Verification Link & Cooldown):**
  - Verification email delivery is performed via Firebase Client Auth (`sendEmailVerification`).
  - Resend cooldown (60 seconds) is enforced on the frontend client (`useVerificationEmailCooldown`).
- **BR-09 (Client Rollback Compensation):**
  - If the backend rejects registration (e.g. duplicate email/phone or policy validation failure), the frontend automatically deletes the newly created Firebase user (`createdFirebaseUser.delete()`) to prevent orphaned accounts in Firebase.
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
| Authorization header missing | 401 | `AUTH_HEADER_MISSING` | Bearer Firebase ID token is required in the Authorization header. |
| Firebase token missing/invalid | 401 | `AUTH_TOKEN_INVALID` / `MSG14` | Invalid or expired Firebase authentication token. |
| Token email does not match | 400 | `AUTH_EMAIL_MISMATCH` | Registration email does not match the email verified in Firebase token. |
| Email not verified in token | 400 | `MSG_EMAIL_NOT_VERIFIED` | Your email address has not been verified yet. Please click the verification link sent to your email. |
| User not found for verification | 404 | `MSG_USER_NOT_FOUND` | User account was not found. Please register first. |
| Resend cooldown active | 429 | `MSG_COOLDOWN` | Please wait before requesting another code. |
| Unverified login attempted | 403 | `MSG_UNVERIFIED` | Please verify your email before signing in. |
| Google ID token invalid | 400 / 401 | `MSG_GOOGLE_TOKEN_INVALID` | Invalid Google authentication token. |
| Generic / server failure | 500 | `MSG127` | Something went wrong. Please try again later. |

---

## 6. API Contract (Backend)

### 6.1 `POST /api/v1/auth/register`

**Headers**
- `Content-Type: application/json`
- `Authorization: Bearer <firebaseIdToken>` (Required)

**Request Body**
```json
{
  "email": "traveler@example.com",
  "password": "Password123!",
  "fullName": "Nguyen Van A",
  "phoneNumber": "0912345678",
  "acceptedTerms": true
}
```

**Success — `201 Created`**
```json
{
  "success": true,
  "statusCode": 201,
  "message": "Account registered successfully! Please check your email for the verification code.",
  "data": {
    "userId": 1,
    "email": "traveler@example.com",
    "fullName": "Nguyen Van A",
    "role": "Traveler",
    "status": "PendingEmailVerification",
    "emailSent": true,
    "messageCode": "MSG07"
  }
}
```
*Note: At this stage, NO session tokens (JWT) are issued. Account status is `PendingEmailVerification`.*

**Errors**
- `401 Unauthorized` — `AUTH_HEADER_MISSING` or `AUTH_TOKEN_INVALID`
- `400 BadRequest` — `AUTH_EMAIL_MISMATCH` or field validation errors (`MSG01`, `MSG02`, `MSG04`, `MSG05`, `MSG_TOS`)
- `409 Conflict` — `MSG03` (email taken) or `MSG_PHONE_DUP` (phone taken)
- `500 InternalServerError` — `MSG127`

---

### 6.2 `POST /api/v1/auth/verify-email`

**Headers**
- `Content-Type: application/json`
- `Authorization: Bearer <refreshedFirebaseIdToken>` (Required; token MUST have claim `email_verified: true`)

**Request Body**
*(Empty)*

**Success — `200 OK`**
```json
{
  "success": true,
  "statusCode": 200,
  "message": "Email verified successfully.",
  "data": {
    "userId": 1,
    "email": "traveler@example.com",
    "status": "Active",
    "emailVerifiedAt": "2026-09-09T11:15:00Z",
    "accessToken": "eyJhbGciOi...",
    "refreshToken": "7b8f9e...",
    "accessTokenExpiresAtUtc": "2026-09-09T12:15:00Z"
  }
}
```
*Verification transitions the account to `Active` and issues TripMate system JWT tokens.*

**Errors**
- `401 Unauthorized` — `AUTH_HEADER_MISSING` or `MSG14` (invalid token)
- `400 BadRequest` — `MSG_EMAIL_NOT_VERIFIED` (email not yet verified in Firebase)
- `404 NotFound` — `MSG_USER_NOT_FOUND`
- `500 InternalServerError` — `MSG127`

---

### 6.3 `POST /api/v1/auth/google`

**Headers**
- `Content-Type: application/json`
- `Authorization: Bearer <token>` *(optional if supplied in body)*

**Request Body**
```json
{
  "idToken": "firebase-or-google-id-token"
}
```
*Note: Backend accepts token either in the JSON body (`idToken`) or via `Authorization: Bearer <token>` header.*

**Validation Mechanism:**
1. **Primary:** Validated server-side using Firebase Admin SDK (`firebaseAuthService.VerifyIdTokenAsync`).
2. **Fallback:** If raw Google OAuth token is provided, validated via `GoogleTokenValidator` using Google APIs.

**Success — `200 OK`**
```json
{
  "success": true,
  "statusCode": 200,
  "message": "Google authentication successful.",
  "data": {
    "userId": 1,
    "status": "Active",
    "accessToken": "eyJhbGciOi...",
    "refreshToken": "7b8f9e...",
    "isNewAccount": true
  }
}
```

**Errors**
- `400 BadRequest` — `AUTH_TOKEN_MISSING`
- `401 Unauthorized` — `MSG_GOOGLE_TOKEN_INVALID`
- `500 InternalServerError` — `MSG127`

---

## 7. End-to-End Flow

### 7.1 Email Registration Flow
1. Guest enters registration details (Name, Email, Phone, Password, Confirm Password, Terms).
2. Frontend creates Firebase user via `createUserWithEmailAndPassword(auth, email, password)`.
3. Frontend acquires Firebase ID token via `user.getIdToken()`.
4. Frontend calls backend `POST /api/v1/auth/register` with `Authorization: Bearer <idToken>`.
   - If backend rejects, frontend calls `user.delete()` to rollback Firebase state.
5. Backend verifies ID token, checks email/phone uniqueness, creates user (`PendingEmailVerification`).
6. Frontend triggers verification email via `sendEmailVerification(user, { url: '/verify-email' })`.
7. Frontend navigates to `/verify-account?email=...` displaying instructions and a 60s cooldown resend button.
8. Guest opens email and clicks verification link, navigating to `/verify-email?mode=verifyEmail&oobCode=...`.
9. `VerifyEmailHandler` calls `applyActionCode(auth, oobCode)` to confirm verification on Firebase.
10. `VerifyEmailHandler` calls `currentUser.reload()` and `currentUser.getIdToken(true)` to obtain a fresh token with `email_verified: true`.
11. `VerifyEmailHandler` sends refreshed token to backend `POST /api/v1/auth/verify-email`.
12. Backend verifies `email_verified == true`, marks account `Active`, generates and returns TripMate access & refresh tokens.
13. Frontend saves tokens in `localStorage` and provides action to proceed.

### 7.2 Google Authentication Flow
1. Guest taps **Continue with Google**.
2. Frontend calls `signInWithPopup(auth, new GoogleAuthProvider())`.
3. Frontend acquires Firebase ID token via `result.user.getIdToken()`.
4. Frontend sends token in Header and Body to `POST /api/v1/auth/google`.
5. Backend verifies token, provisions or activates Traveler record, and issues TripMate system JWT tokens.
6. Frontend saves tokens and redirects to Home (`/`).

### 7.3 Cross-Browser / Multi-Device Email Verification Fallback
If the user opens the verification email link on a different browser or device where `auth.currentUser` is `null`:
1. `VerifyEmailHandler` verifies the code via Firebase `applyActionCode`, but detects `currentUser == null`.
2. It displays an informative screen: *"Your email was verified with Firebase. Please sign in to finish activation."*
3. User navigates to `/sign-in` and submits credentials.
4. Backend returns `403 MSG_UNVERIFIED`.
5. Frontend automatically executes Firebase sign-in (`signInWithEmailAndPassword`), detects `user.emailVerified == true`, fetches fresh ID token, calls `POST /api/v1/auth/verify-email`, activates account, and completes login seamlessly.

---

## 8. Backend Implementation Details (Clean Architecture)

- **Domain Entity (`User`):** Contains `Email`, `PhoneNumber`, `FullName`, `PasswordHash`, `Role`, `Status`, `AvatarUrl`, `EmailVerifiedAtUtc`.
- **Application Handlers:**
  - `RegisterTravelerCommandHandler`: Validates Bearer token via `IFirebaseAuthService`, checks email match, ensures uniqueness, creates `User` (`PendingEmailVerification`).
  - `VerifyEmailCommandHandler`: Validates Bearer token, asserts `tokenResult.EmailVerified == true`, activates `User` (`Active`), persists refresh token, generates JWT access token.
  - `GoogleAuthCommandHandler`: Validates token via `IFirebaseAuthService` (with fallback to `IGoogleTokenValidator`), provisions/activates user, issues session tokens.
- **Infrastructure Services:**
  - `FirebaseAuthService`: Implements `IFirebaseAuthService` using the Firebase Admin SDK as the sole trusted verifier of Firebase ID tokens (`VerifyIdTokenAsync` is the only acceptance path). If Firebase Admin verification is unavailable (credentials not configured) or the token fails verification, authentication fails closed — no local/development JWT decode fallback exists.
  - `JwtTokenService`: Generates HS256 access tokens and cryptographically secure random refresh tokens with SHA-256 hashing.
