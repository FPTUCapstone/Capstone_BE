using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

/// <summary>
/// Validates Google ID Tokens server-side using Google.Apis.Auth.
/// Verifies signature, audience (client_id), issuer, and expiry against
/// Google's public key endpoint — not just a local JWT decode.
/// </summary>
public class GoogleTokenValidator(
    IConfiguration configuration,
    ILogger<GoogleTokenValidator> logger) : IGoogleTokenValidator
{
    public async Task<GoogleTokenPayload?> ValidateAsync(string idToken, CancellationToken cancellationToken = default)
    {
        try
        {
            var clientId = configuration["Google:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId))
            {
                logger.LogError("Google:ClientId is not configured. Cannot validate Google ID Token.");
                return null;
            }

            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = [clientId]
            };

            // Verifies signature against Google's public keys, audience, issuer, and expiry.
            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);

            if (string.IsNullOrWhiteSpace(payload.Email) || string.IsNullOrWhiteSpace(payload.Subject))
            {
                logger.LogWarning("Google ID Token missing email or sub claim after validation.");
                return null;
            }

            var name = payload.Name ?? payload.Email;
            var picture = payload.Picture;

            return new GoogleTokenPayload(payload.Email, payload.Subject, name, picture);
        }
        catch (InvalidJwtException)
        {
            // Fallback: If token is an OAuth2 Access Token (e.g. from Flutter Web google_sign_in), validate via Google userinfo
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

                var response = await httpClient.GetAsync("https://www.googleapis.com/oauth2/v3/userinfo", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    var root = doc.RootElement;

                    var email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
                    var sub = root.TryGetProperty("sub", out var subProp) ? subProp.GetString() : null;
                    var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : email;
                    var picture = root.TryGetProperty("picture", out var picProp) ? picProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(sub))
                    {
                        return new GoogleTokenPayload(email, sub, name ?? email, picture);
                    }
                }
            }
            catch (Exception reqEx)
            {
                logger.LogWarning(reqEx, "Failed to validate Google access token via userinfo endpoint.");
            }

            logger.LogWarning("Google ID Token failed validation: not a valid JWT and not an authorized access token.");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error validating Google ID Token.");
            return null;
        }
    }
}
