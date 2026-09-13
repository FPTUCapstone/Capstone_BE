# UC-01 Backend Implementation Plan: Register Traveler Account & Email Verification (Rev. 3)

> **Specification Reference:** [`Capstone_BE/specs/UC-01-spec.md`](file:///d:/FPTUCapstone/Capstone_BE/specs/UC-01-spec.md)
> **Identity Provider:** Firebase Authentication (Client SDK on FE, Firebase Admin SDK on BE)

---

## Granular Atomic Task Breakdown & Implementation Status

### BE-UC01-01: Define DTOs, Commands, Response Models, and Error Codes
- **Status:** [COMPLETED]
- **Target Files:**
  - `src/TripMate.Application/Features/Authentication/Register/RegisterTravelerCommand.cs`
  - `src/TripMate.Application/Features/Authentication/Register/RegisterTravelerResponse.cs`
  - `src/TripMate.Application/Features/Authentication/VerifyEmail/VerifyEmailCommand.cs`
  - `src/TripMate.Application/Features/Authentication/VerifyEmail/VerifyEmailResponse.cs`
  - `src/TripMate.Application/Features/Authentication/GoogleAuth/GoogleAuthCommand.cs`
  - `src/TripMate.Application/Features/Authentication/GoogleAuth/GoogleAuthResponse.cs`
  - `src/TripMate.Application/Features/Authentication/Common/AuthErrorCodes.cs`
- **Implementation Details:**
  - Strongly-typed records for registration, verification, and Google auth.
  - Included `FirebaseIdToken` in commands for bearer token authorization.
  - Standardized error codes: `MSG01`–`MSG07`, `MSG_PHONE_DUP`, `MSG_TOS`, `MSG_UNVERIFIED`, `AUTH_HEADER_MISSING`, `AUTH_TOKEN_MISSING`, `AUTH_TOKEN_INVALID`, `AUTH_EMAIL_MISMATCH`, `MSG_EMAIL_NOT_VERIFIED`, `MSG_USER_NOT_FOUND`, `MSG_GOOGLE_TOKEN_INVALID`, `MSG127`.

### BE-UC01-02: Implement Validators and Field Rules
- **Status:** [COMPLETED]
- **Target Files:**
  - `src/TripMate.Application/Features/Authentication/Register/RegisterTravelerCommandValidator.cs`
  - `src/TripMate.Application/Features/Authentication/GoogleAuth/GoogleAuthCommandValidator.cs`
- **Implementation Details:**
  - FluentValidation rules encoding password policy (BR-02: 8+ chars, upper, lower, digit, special).
  - Phone format regex `^0\d{9}$`.
  - Terms acceptance requirement (`AcceptedTerms == true`).
  - Validation of Firebase ID token presence.

### BE-UC01-03: Define Authentication Service Interfaces
- **Status:** [COMPLETED]
- **Target Files:**
  - `src/TripMate.Application/Common/Interfaces/IFirebaseAuthService.cs`
  - `src/TripMate.Application/Common/Interfaces/IGoogleTokenValidator.cs`
  - `src/TripMate.Application/Common/Interfaces/IJwtTokenService.cs`
  - `src/TripMate.Application/Common/Interfaces/IPasswordHasherService.cs`
- **Implementation Details:**
  - `IFirebaseAuthService.VerifyIdTokenAsync(string idToken, CancellationToken ct)` returning `FirebaseTokenValidationResult(Uid, Email, EmailVerified, FullName, Picture)`.
  - `IGoogleTokenValidator.ValidateAsync(string token, CancellationToken ct)` for direct Google token fallback.
  - `IJwtTokenService` for system JWT and refresh token generation/hashing.

### BE-UC01-04: Implement Firebase Auth Service
- **Status:** [COMPLETED]
- **Target Files:**
  - `src/TripMate.Infrastructure/Services/FirebaseAuthService.cs`
- **Implementation Details:**
  - Integrates official Firebase Admin SDK (`FirebaseApp`, `FirebaseAuth.DefaultInstance.VerifyIdTokenAsync`) as the sole trusted verifier of Firebase ID tokens.
  - Configures credentials via `GOOGLE_APPLICATION_CREDENTIALS` or `FIREBASE_SERVICE_ACCOUNT_KEY_JSON`.
  - Fails closed when the Admin SDK is unavailable or verification fails — no `ReadJwtToken`/`JwtSecurityTokenHandler` decode fallback exists in any environment (SEC-01).

### BE-UC01-05: Implement Google Token Validator with UserInfo Fallback
- **Status:** [COMPLETED]
- **Target Files:**
  - `src/TripMate.Infrastructure/Services/GoogleTokenValidator.cs`
- **Implementation Details:**
  - Validates Google JWT ID tokens using Google libraries (`GoogleJsonWebSignature.ValidateAsync`).
  - Fallback to Google OAuth2 userinfo endpoint for access tokens.

### BE-UC01-06: Implement Register Traveler Handler
- **Status:** [COMPLETED]
- **Target Files:**
  - `src/TripMate.Application/Features/Authentication/Register/RegisterTravelerCommandHandler.cs`
- **Implementation Details:**
  - Verifies incoming Firebase ID token and matches against registration email.
  - Enforces BR-01 (Email uniqueness) and BR-01b (Phone uniqueness).
  - Hashes password using BCrypt.
  - Creates `User` with `Status = AccountStatus.PendingEmailVerification`.
  - Returns `RegisterTravelerResponse` (no session tokens issued).

### BE-UC01-07: Implement Verify Email Handler
- **Status:** [COMPLETED]
- **Target Files:**
  - `src/TripMate.Application/Features/Authentication/VerifyEmail/VerifyEmailCommandHandler.cs`
- **Implementation Details:**
  - Verifies refreshed Firebase ID token via `IFirebaseAuthService`.
  - Asserts `tokenResult.EmailVerified == true`.
  - Transitions `User.Status` to `AccountStatus.Active`, sets `EmailVerifiedAtUtc`.
  - Generates opaque refresh token, hashes with SHA-256 and stores in `dbo.RefreshTokens`.
  - Generates TripMate JWT access token and returns in `VerifyEmailResponse`.

### BE-UC01-08: Implement Google Auth Handler
- **Status:** [COMPLETED]
- **Target Files:**
  - `src/TripMate.Application/Features/Authentication/GoogleAuth/GoogleAuthCommandHandler.cs`
- **Implementation Details:**
  - Primary verification via `IFirebaseAuthService`, fallback to `IGoogleTokenValidator`.
  - Auto-provisions new `User` with status `Active` (`isNewAccount = true`) or activates existing user (`isNewAccount = false`).
  - Updates profile picture / avatar if present.
  - Generates and stores refresh token and issues system JWT access token.

### BE-UC01-09: Expose AuthController Endpoints
- **Status:** [COMPLETED]
- **Target Files:**
  - `src/TripMate.Api/Controllers/V1/AuthController.cs`
- **Implementation Details:**
  - `POST /api/v1/auth/register`: Extracts Bearer token, sends `RegisterTravelerCommand`. Returns 201 Created.
  - `POST /api/v1/auth/verify-email`: Extracts Bearer token, sends `VerifyEmailCommand`. Returns 200 OK.
  - `POST /api/v1/auth/google`: Extracts token from body `idToken` or header Bearer fallback, sends `GoogleAuthCommand`. Returns 200 OK.
  - `POST /api/v1/auth/login`: Email/password login with unverified account guard returning `403 MSG_UNVERIFIED`.

### BE-UC01-10: Configure Dependency Injection & Options
- **Status:** [COMPLETED]
- **Target Files:**
  - `src/TripMate.Infrastructure/DependencyInjection.cs`
- **Implementation Details:**
  - Registers `IFirebaseAuthService -> FirebaseAuthService`.
  - Registers `IGoogleTokenValidator -> GoogleTokenValidator`.
  - Registers `IJwtTokenService` and `IPasswordHasherService`.

### BE-UC01-11: Application Unit Tests
- **Status:** [COMPLETED]
- **Target Files:**
  - `tests/TripMate.Application.UnitTests/Features/Authentication/Register/RegisterTravelerCommandHandlerTests.cs`
  - `tests/TripMate.Application.UnitTests/Features/Authentication/VerifyEmail/VerifyEmailCommandHandlerTests.cs`
  - `tests/TripMate.Application.UnitTests/Features/Authentication/Login/LoginCommandHandlerTests.cs`
- **Implementation Details:**
  - Tests covering Firebase token verification failure, unverified email rejection, valid email verification activating user and issuing JWT, duplicate email/phone conflict handling, and login state guards.
  - All 41 unit tests passing.
