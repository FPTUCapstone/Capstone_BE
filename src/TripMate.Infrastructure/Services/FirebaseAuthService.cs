using System.IdentityModel.Tokens.Jwt;
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
                        _logger.LogWarning("Application Default Credentials not found. Local fallback JWT verification enabled for Firebase tokens.");
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
            _logger.LogWarning(ex, "Failed to initialize FirebaseAdmin FirebaseAuth; fallback token verification will be used.");
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

                var name = decoded.Claims.TryGetValue("name", out var nameClaim) ? nameClaim?.ToString() : null;
                var picture = decoded.Claims.TryGetValue("picture", out var picClaim) ? picClaim?.ToString() : null;

                return new FirebaseTokenValidationResult(uid, email, emailVerified, name, picture);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FirebaseAdmin VerifyIdTokenAsync failed. Checking token format.");
                throw;
            }
        }

        // 2. Dev Fallback: Validate token using standard JwtSecurityTokenHandler
        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(idToken))
        {
            throw new InvalidOperationException("Invalid Firebase ID token format.");
        }

        var jwt = handler.ReadJwtToken(idToken);
        var expectedIssuer = $"https://securetoken.google.com/{_projectId}";

        if (!string.Equals(jwt.Issuer, expectedIssuer, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid token issuer. Expected {expectedIssuer}, got {jwt.Issuer}.");
        }

        if (jwt.ValidTo < DateTime.UtcNow)
        {
            throw new InvalidOperationException("Firebase ID token has expired.");
        }

        var sub = jwt.Subject ?? jwt.Claims.FirstOrDefault(c => c.Type == "user_id" || c.Type == "sub")?.Value;
        if (string.IsNullOrEmpty(sub))
        {
            throw new InvalidOperationException("Firebase ID token missing subject (uid).");
        }

        var tokenEmail = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value ?? string.Empty;
        var tokenEmailVerifiedClaim = jwt.Claims.FirstOrDefault(c => c.Type == "email_verified")?.Value;
        var tokenEmailVerified = bool.TryParse(tokenEmailVerifiedClaim, out var isVerified) && isVerified;
        var tokenName = jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
        var tokenPicture = jwt.Claims.FirstOrDefault(c => c.Type == "picture")?.Value;

        return new FirebaseTokenValidationResult(sub, tokenEmail, tokenEmailVerified, tokenName, tokenPicture);
    }
}
