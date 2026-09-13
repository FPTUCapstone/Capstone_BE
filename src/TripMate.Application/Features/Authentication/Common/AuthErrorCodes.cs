namespace TripMate.Application.Features.Authentication.Common;

public static class AuthErrorCodes
{
    public const string Msg01 = "MSG01"; // Required field empty
    public const string Msg02 = "MSG02"; // Invalid email format
    public const string Msg03 = "MSG03"; // Email already registered
    public const string Msg04 = "MSG04"; // Invalid phone format
    public const string MsgPhoneDup = "MSG_PHONE_DUP"; // Phone already registered
    public const string Msg05 = "MSG05"; // Password fails policy
    public const string Msg06 = "MSG06"; // Confirm password mismatch
    public const string MsgTos = "MSG_TOS"; // Terms not accepted
    public const string Msg07 = "MSG07"; // Registration success
    public const string Msg14 = "MSG14"; // OTP incorrect or expired
    public const string MsgCooldown = "MSG_COOLDOWN"; // Resend cooldown active
    public const string MsgUnverified = "MSG_UNVERIFIED"; // Email registered but unverified login attempt
    public const string MsgEmailSendFailed = "MSG_EMAIL_SEND_FAILED"; // Account created but email delivery failed
    public const string MsgOtpAttemptsExceeded = "MSG_OTP_ATTEMPTS_EXCEEDED"; // 5 failed OTP attempts
    public const string MsgGoogleTokenInvalid = "MSG_GOOGLE_TOKEN_INVALID"; // Google ID token invalid
    public const string MsgResendSuccess = "MSG_RESEND_SUCCESS"; // OTP resend success
    public const string Msg127 = "MSG127"; // Generic/server failure
    public const string AuthTokenMissing = "AUTH_TOKEN_MISSING";
    public const string AuthTokenInvalid = "AUTH_TOKEN_INVALID";
    public const string AuthEmailMismatch = "AUTH_EMAIL_MISMATCH";

    // Semantic aliases
    public const string RegisterSuccess = Msg07;
    public const string VerificationEmailSent = MsgResendSuccess;
    public const string EmailAlreadyExists = Msg03;
    public const string FullNameRequired = Msg01;
    public const string EmailRequired = Msg01;
    public const string EmailInvalid = Msg02;
    public const string PasswordRequired = Msg01;
    public const string PasswordPolicyInvalid = Msg05;
    public const string PasswordConfirmMismatch = Msg06;
    public const string PhoneInvalid = Msg04;
    public const string PhoneAlreadyExists = MsgPhoneDup;
    public const string TermsRequired = MsgTos;
    public const string OtpInvalid = Msg14;
    public const string OtpResendCooldown = MsgCooldown;
    public const string OtpResendSuccess = MsgResendSuccess;
    public const string GoogleTokenInvalid = MsgGoogleTokenInvalid;
    public const string ServerError = Msg127;

    // Backwards compatibility aliases
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string AccountPendingVerification = MsgUnverified;
    public const string AccountLocked = "auth.account_locked";
    public const string AccountInactive = "auth.account_inactive";
    public const string EmailAlreadyRegistered = Msg03;
}
