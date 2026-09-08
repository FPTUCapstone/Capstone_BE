# UC-01 Backend Implementation Plan: Register Traveler Account & Email Verification (Rev. 2)

> **Specification Reference:** [`Capstone_BE/specs/UC-01-spec.md`](file:///d:/FPTUCapstone/Capstone_BE/specs/UC-01-spec.md)

## Granular Atomic Task Breakdown

### BE-UC01-01: Define DTOs, Commands, Response Models, and Error Codes
- **Files to Modify/Create**:
  - `src/TripMate.Application/Features/Authentication/Register/RegisterTravelerCommand.cs`
  - `src/TripMate.Application/Features/Authentication/Register/RegisterTravelerResponse.cs`
  - `src/TripMate.Application/Features/Authentication/VerifyEmail/VerifyEmailCommand.cs`
  - `src/TripMate.Application/Features/Authentication/ResendOtp/ResendOtpCommand.cs`
  - `src/TripMate.Application/Features/Authentication/ResendOtp/ResendOtpResponse.cs`
  - `src/TripMate.Application/Features/Authentication/GoogleAuth/GoogleAuthCommand.cs`
  - `src/TripMate.Application/Features/Authentication/GoogleAuth/GoogleAuthResponse.cs`
  - `src/TripMate.Application/Features/Authentication/Common/AuthErrorCodes.cs`
- **Details**: Define strongly-typed sealed records for all request/response models. Include `emailSent` and `messageCode` fields. Map all error codes (`MSG01`-`MSG07`, `MSG14`, `MSG_TOS`, `MSG_PHONE_DUP`, `MSG_COOLDOWN`, `MSG_UNVERIFIED`, `MSG_EMAIL_SEND_FAILED`, `MSG_OTP_ATTEMPTS_EXCEEDED`, `MSG_GOOGLE_TOKEN_INVALID`, `MSG_RESEND_SUCCESS`, `MSG127`).

### BE-UC01-02: Implement Validators and Field Normalization
- **Files to Modify/Create**:
  - `src/TripMate.Application/Features/Authentication/Register/RegisterTravelerCommandValidator.cs`
  - `src/TripMate.Application/Features/Authentication/VerifyEmail/VerifyEmailCommandValidator.cs`
  - `src/TripMate.Application/Features/Authentication/ResendOtp/ResendOtpCommandValidator.cs`
  - `src/TripMate.Application/Features/Authentication/GoogleAuth/GoogleAuthCommandValidator.cs`
- **Details**: Implement FluentValidation validators encoding BR-02 password policy, phone format (`^0\d{9}$`), Terms acceptance (`acceptedTerms == true`), and confirm password match.

### BE-UC01-03: Define Authentication Service Interfaces
- **Files to Modify/Create**:
  - `src/TripMate.Application/Common/Interfaces/IEmailSender.cs`
  - `src/TripMate.Application/Common/Interfaces/IOtpService.cs`
  - `src/TripMate.Application/Common/Interfaces/IGoogleTokenValidator.cs`
- **Details**:
  - `IEmailSender`: `SendVerificationCodeAsync(string email, string otpCode, CancellationToken ct)`
  - `IOtpService`: `GenerateAndStoreAsync(long userId, CancellationToken ct)`, `ValidateAndConsumeAsync(long userId, string code, CancellationToken ct)`, `CanResendAsync(long userId, CancellationToken ct)`
  - `IGoogleTokenValidator`: `ValidateAsync(string idToken, CancellationToken ct)`
  - Note: Reuse existing `IPasswordHasher` and `IJwtTokenService`.

### BE-UC01-04: Implement Redis OTP Service
- **Files to Modify/Create**:
  - `src/TripMate.Infrastructure/Services/RedisOtpService.cs`
- **Details**: Implement Redis-backed OTP store using key `otp:{userId}`. Hash OTP with SHA-256 at rest, enforce 10-min TTL, track `AttemptCount` (max 5), enforce 60s resend cooldown (`LastSentAt`), and make consumption single-use.

### BE-UC01-05: Implement Firebase Auth Service
- **Files to Modify/Create**:
  - `src/TripMate.Infrastructure/Services/FirebaseAuthService.cs`
- **Details**: Integrate Firebase Admin SDK and fallback RS256 token verification to validate Firebase ID tokens and extract verified uid, email, and emailVerified claim.

### BE-UC01-06: Implement Google Token Validator with UserInfo Fallback
- **Files to Modify/Create**:
  - `src/TripMate.Infrastructure/Services/GoogleTokenValidator.cs`
- **Details**: Integrate Google JSON Web Token verification library to validate `idToken` against Google's public key endpoint and audience (`GoogleJsonWebSignature`). Include a transparent fallback to Google's standard OAuth2 `https://www.googleapis.com/oauth2/v3/userinfo` endpoint with Bearer authentication when the incoming token is an OAuth2 Access Token (e.g., from Flutter Web / Google Identity Services).

### BE-UC01-07: Implement Register Traveler Handler
- **Files to Modify/Create**:
  - `src/TripMate.Application/Features/Authentication/Register/RegisterTravelerCommandHandler.cs`
- **Details**: Enforce BR-01/BR-01b email/phone uniqueness, BCrypt hashing, atomic DB transaction for `User` + `TravelerProfile` (status `PendingEmailVerification`). Commit DB transaction BEFORE calling Resend. Generate OTP and store in Redis. Return `RegisterTravelerResponse`.

### BE-UC01-08: Implement Verify Email Handler
- **Files to Modify/Create**:
  - `src/TripMate.Application/Features/Authentication/VerifyEmail/VerifyEmailCommandHandler.cs`
- **Details**: Resolve `email` $\rightarrow$ `userId`, validate code against Redis `otp:{userId}` (atomic single-use), update `Users.status = 'Active'`, set `EmailVerifiedAtUtc`, issue JWT access token and refresh token in `dbo.RefreshTokens`.

### BE-UC01-09: Implement Resend OTP Handler
- **Files to Modify/Create**:
  - `src/TripMate.Application/Features/Authentication/ResendOtp/ResendOtpCommandHandler.cs`
- **Details**: Validate account is not active, check 60s cooldown (`MSG_COOLDOWN`), invalidate previous OTP, generate new OTP in Redis, and trigger Resend call.

### BE-UC01-10: Implement Google Auth Handler
- **Files to Modify/Create**:
  - `src/TripMate.Application/Features/Authentication/GoogleAuth/GoogleAuthCommandHandler.cs`
- **Details**: Execute BR-06 decision table: validate Google ID Token, query `dbo.AuthProviders`, handle new accounts (`isNewAccount: true`), existing Google linked accounts (`isNewAccount: false`), and existing password accounts (`409 MSG03`).

### BE-UC01-11: Expose AuthController Endpoints
- **Files to Modify/Create**:
  - `src/TripMate.Api/Controllers/V1/AuthController.cs`
- **Details**: Add `POST /register`, `POST /verify-email`, `POST /resend-otp`, `POST /google` endpoints with `[AllowAnonymous]`.

### BE-UC01-12: Configure Dependency Injection & Options
- **Files to Modify/Create**:
  - `src/TripMate.Infrastructure/DependencyInjection.cs`
  - `src/TripMate.Api/Program.cs`
- **Details**: Register `IEmailSender`, `IOtpService`, `IGoogleTokenValidator`, configure Redis connection string and Resend API key options.

### BE-UC01-13: Add Application Unit Tests
- **Files to Modify/Create**:
  - `tests/TripMate.Application.UnitTests/Features/Authentication/Register/RegisterTravelerCommandHandlerTests.cs`
  - `tests/TripMate.Application.UnitTests/Features/Authentication/VerifyEmail/VerifyEmailCommandHandlerTests.cs`
  - `tests/TripMate.Application.UnitTests/Features/Authentication/ResendOtp/ResendOtpCommandHandlerTests.cs`
  - `tests/TripMate.Application.UnitTests/Features/Authentication/GoogleAuth/GoogleAuthCommandHandlerTests.cs`
- **Details**: Unit test all handlers for success, validation failure, 60s cooldown, 5-attempt lockout, and decision table branches.

### BE-UC01-14: Add Integration Tests
- **Files to Modify/Create**:
  - `tests/TripMate.Api.IntegrationTests/Authentication/RegisterEndpointTests.cs`
  - `tests/TripMate.Api.IntegrationTests/Authentication/VerifyEmailEndpointTests.cs`
  - `tests/TripMate.Api.IntegrationTests/Authentication/ResendOtpEndpointTests.cs`
  - `tests/TripMate.Api.IntegrationTests/Authentication/GoogleAuthEndpointTests.cs`
- **Details**: Test end-to-end API endpoints with DB transactions, SQL unique index constraints (`UX_Users_Email`, `UX_Users_Phone`), and controller ProblemDetails mappings.

### BE-UC01-15: Add Security & Concurrency Tests
- **Files to Modify/Create**:
  - `tests/TripMate.Api.IntegrationTests/Authentication/AuthSecurityAndConcurrencyTests.cs`
- **Details**: Test concurrent registration with same email/phone, concurrent OTP verification (single-use enforcement), atomic User+TravelerProfile rollback, and assert no sensitive data (passwords, OTPs, tokens) appears in logs.
