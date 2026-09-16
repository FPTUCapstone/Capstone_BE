using FirebaseAdmin;
using FirebaseAdmin.Auth;

using Google.Apis.Auth.OAuth2;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

public class FirebaseAuthService : IFirebaseAuthService
{
    private readonly FirebaseAuth? _firebaseAuth;
    private readonly string _projectId;
    private readonly ILogger<FirebaseAuthService> _logger;

    public FirebaseAuthService(IConfiguration configuration, ILogger<FirebaseAuthService> logger)
    {
        _logger = logger;
        _projectId = configuration["Firebase:ProjectId"] ?? "tripmate-82be3";

        try
        {
            if (FirebaseApp.DefaultInstance == null)
            {
                var credentialsPath = configuration["GOOGLE_APPLICATION_CREDENTIALS"]
                    ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

                var credentialsJson = configuration["FIREBASE_SERVICE_ACCOUNT_KEY_JSON"]
                    ?? configuration["Firebase:ServiceAccountKeyJson"];

                AppOptions? options = null;

                if (!string.IsNullOrWhiteSpace(credentialsJson))
                {
                    options = new AppOptions
                    {
                        Credential = GoogleCredential.FromJson(credentialsJson),
                        ProjectId = _projectId
                    };
                }
                else if (!string.IsNullOrWhiteSpace(credentialsPath) && File.Exists(credentialsPath))
                {
                    options = new AppOptions
                    {
                        Credential = GoogleCredential.FromFile(credentialsPath),
                        ProjectId = _projectId
                    };
                }
                else
                {
                    try
                    {
                        options = new AppOptions
                        {
                            Credential = GoogleCredential.GetApplicationDefault(),
                            ProjectId = _projectId
                        };
                    }
                    catch
                    {
                        _logger.LogWarning("Application Default Credentials not found. Firebase Admin verification is unavailable; Firebase ID tokens will be rejected until credentials are configured.");
                    }
                }

                if (options != null)
                {
                    FirebaseApp.Create(options);
                }
            }

            if (FirebaseApp.DefaultInstance != null)
            {
                _firebaseAuth = FirebaseAuth.DefaultInstance;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize FirebaseAdmin FirebaseAuth. Firebase ID token verification is unavailable; tokens will be rejected until credentials are configured.");
        }
    }

    public async Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            throw new ArgumentException("Firebase ID token cannot be empty.", nameof(idToken));
        }

        // 1. Primary: Use official Firebase Admin SDK if initialized
        if (_firebaseAuth != null)
        {
            try
            {
                var decoded = await _firebaseAuth.VerifyIdTokenAsync(idToken, cancellationToken);
                var uid = decoded.Uid;
                var email = decoded.Claims.TryGetValue("email", out var emailClaim) ? emailClaim?.ToString() ?? string.Empty : string.Empty;
                var emailVerified = decoded.Claims.TryGetValue("email_verified", out var evClaim) && evClaim is bool ev && ev;
                var signInProvider = ExtractSignInProvider(decoded.Claims);

                var name = decoded.Claims.TryGetValue("name", out var nameClaim) ? nameClaim?.ToString() : null;
                var picture = decoded.Claims.TryGetValue("picture", out var picClaim) ? picClaim?.ToString() : null;

                return new FirebaseTokenValidationResult(uid, email, emailVerified, name, picture, signInProvider);
            }
            catch (FirebaseAuthException ex)
            {
                // A FirebaseAuthException is a verification REJECTION of the submitted token
                // (bad signature, expired, wrong audience...) — the handler maps it to 401.
                _logger.LogWarning(ex, "FirebaseAdmin VerifyIdTokenAsync rejected the token.");
                throw;
            }
            catch (Exception ex)
            {
                // Anything else (network, IO, SDK state) means verification could not be
                // performed at all — fail closed as infrastructure unavailability (UC-04 BR-16).
                _logger.LogError(ex, "Firebase infrastructure failure during VerifyIdTokenAsync.");
                throw new FirebaseUnavailableException(
                    "Firebase ID token verification is temporarily unavailable.",
                    ex);
            }
        }

        // Fail closed: the Firebase Admin SDK is the only trusted verifier for Firebase
        // ID tokens. Without it there is no way to validate signatures, so authentication
        // must not proceed — no local JWT decoding or claim-only acceptance is permitted.
        throw new FirebaseUnavailableException(
            "Firebase Admin SDK is not configured; Firebase ID token verification is unavailable. Authentication cannot proceed.");
    }

    /// <summary>
    /// Extracts the nested <c>firebase.sign_in_provider</c> claim (UC-04 BR-11). A missing or
    /// malformed claim yields <c>null</c> so downstream rules can fail closed; this method never
    /// invents a provider value. FirebaseAdmin (via Google.Apis) deserializes the nested
    /// <c>firebase</c> object with Newtonsoft.Json — it surfaces as a <c>JObject</c>; other
    /// JSON-representable shapes are normalized via round-tripping as a fallback.
    /// </summary>
    public static string? ExtractSignInProvider(IReadOnlyDictionary<string, object> claims)
    {
        if (!claims.TryGetValue("firebase", out var firebaseClaim) || firebaseClaim is null)
        {
            return null;
        }

        if (firebaseClaim is Newtonsoft.Json.Linq.JObject firebaseObject
            && firebaseObject["sign_in_provider"] is Newtonsoft.Json.Linq.JToken providerToken
            && providerToken.Type == Newtonsoft.Json.Linq.JTokenType.String)
        {
            return (string?)providerToken;
        }

        // Fallback: normalize any other JSON-representable shape via round-tripping.
        try
        {
            var element = System.Text.Json.JsonSerializer.SerializeToElement(firebaseClaim);
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object
                && element.TryGetProperty("sign_in_provider", out var providerElement)
                && providerElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return providerElement.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unserializable claim shape — treated as missing so BR-11 fails closed.
        }

        return null;
    }
}