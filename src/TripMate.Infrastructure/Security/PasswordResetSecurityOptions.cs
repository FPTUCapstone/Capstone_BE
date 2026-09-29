namespace TripMate.Infrastructure.Security;

public class PasswordResetSecurityOptions
{
    public const string SectionName = "PasswordResetSecurity";

    /// <summary>
    /// HMAC pepper for OTP protection. Secret: supply via User Secrets/environment only —
    /// never appsettings files, never source control, never logs.
    /// </summary>
    public string OtpPepper { get; set; } = string.Empty;
}