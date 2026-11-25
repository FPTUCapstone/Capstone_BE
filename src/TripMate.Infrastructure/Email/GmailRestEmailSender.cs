using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MimeKit;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Infrastructure.Email;

public sealed class GmailRestEmailSender : IEmailSender, IEmailVerificationSender, IUserAdministrationNotificationEmailSender
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string SendEndpoint = "https://gmail.googleapis.com/gmail/v1/users/me/messages/send";
    private const string CacheKey = "gmail_api_access_token";

    private readonly HttpClient httpClient;
    private readonly GmailApiOptions options;
    private readonly IMemoryCache cache;
    private readonly ILogger<GmailRestEmailSender> logger;

    public GmailRestEmailSender(
        HttpClient httpClient,
        IOptions<GmailApiOptions> options,
        IMemoryCache cache,
        ILogger<GmailRestEmailSender> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.cache = cache;
        this.logger = logger;
    }

    public async Task<EmailDeliveryResult> SendPasswordResetOtpAsync(
        string destinationEmail,
        string otp,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(otp);

        var content = PasswordResetEmailComposer.Compose(otp);
        return await SendAsync(destinationEmail, content, "Password reset", cancellationToken);
    }

    async Task<EmailDeliveryResult> IEmailVerificationSender.SendAsync(
        string destinationEmail,
        string verificationLink,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationLink);

        var content = EmailVerificationEmailComposer.Compose(verificationLink);
        return await SendAsync(destinationEmail, content, "Email verification", cancellationToken);
    }

    async Task<EmailDeliveryResult> IUserAdministrationNotificationEmailSender.SendAsync(
        string destinationEmail,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var content = new PasswordResetEmailContent(subject, body);
        return await SendAsync(destinationEmail, content, "Admin notification", cancellationToken);
    }

    private async Task<EmailDeliveryResult> SendAsync(
        string destinationEmail,
        PasswordResetEmailContent content,
        string purpose,
        CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                logger.LogError("Failed to acquire access token for Gmail REST API.");
                return EmailDeliveryResult.DefiniteFailure;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(options.FromName, options.SenderEmail));
            message.To.Add(new MailboxAddress(destinationEmail, destinationEmail));
            message.Subject = content.Subject;
            message.Body = new TextPart("plain") { Text = content.TextBody };

            using var stream = new MemoryStream();
            await message.WriteToAsync(stream, cancellationToken);
            var rawBase64Url = Convert.ToBase64String(stream.ToArray())
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            using var request = new HttpRequestMessage(HttpMethod.Post, SendEndpoint)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
                Content = JsonContent.Create(new { raw = rawBase64Url })
            };

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("{Purpose} email delivered via Gmail REST API.", purpose);
                return EmailDeliveryResult.Delivered;
            }

            logger.LogWarning(
                "{Purpose} email failed via Gmail REST API. StatusCode: {StatusCode}",
                purpose,
                response.StatusCode);

            return EmailDeliveryResult.DefiniteFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Gmail REST API email delivery outcome unknown ({ExceptionType}).", ex.GetType().Name);
            return EmailDeliveryResult.Unknown;
        }
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out string? cachedToken) && !string.IsNullOrWhiteSpace(cachedToken))
        {
            return cachedToken;
        }

        var formParams = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["refresh_token"] = options.RefreshToken,
            ["grant_type"] = "refresh_token"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formParams)
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Failed to refresh Gmail API token. Status: {Status}", response.StatusCode);
            return null;
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken);
        if (tokenResponse?.AccessToken is null)
        {
            return null;
        }

        var cacheDuration = TimeSpan.FromSeconds(Math.Max(300, (tokenResponse.ExpiresIn ?? 3600) - 300));
        cache.Set(CacheKey, tokenResponse.AccessToken, cacheDuration);

        return tokenResponse.AccessToken;
    }

    private sealed record GoogleTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("token_type")] string? TokenType);
}