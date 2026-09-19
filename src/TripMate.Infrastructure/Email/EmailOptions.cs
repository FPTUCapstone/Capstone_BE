namespace TripMate.Infrastructure.Email;

/// <summary>Non-secret and secret SMTP settings for outbound email. Secret values
/// (Username/Password/FromEmail) must come from User Secrets or environment configuration, never
/// from tracked appsettings. Password is intentionally not exposed via ToString()/logging.</summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    public string SmtpHost { get; set; } = "smtp.gmail.com";

    public int SmtpPort { get; set; } = 587;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FromEmail { get; set; } = string.Empty;

    public string FromName { get; set; } = "TripMate";
}