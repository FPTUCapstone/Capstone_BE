namespace TripMate.Infrastructure.Email;

public class GmailApiOptions
{
    public const string SectionName = "Gmail";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public string SenderEmail { get; set; } = "tripmate.support.mail@gmail.com";

    public string FromName { get; set; } = "TripMate";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(RefreshToken);
}
