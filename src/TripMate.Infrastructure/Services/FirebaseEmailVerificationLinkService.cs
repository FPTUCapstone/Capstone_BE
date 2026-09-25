using FirebaseAdmin;
using FirebaseAdmin.Auth;

using Google.Apis.Auth.OAuth2;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

public sealed class FirebaseEmailVerificationLinkService : IEmailVerificationLinkService, IEmailVerificationStatusService
{
    private readonly FirebaseAuth? firebaseAuth;
    private readonly string continueUrl;
    private readonly ILogger<FirebaseEmailVerificationLinkService> logger;

    public FirebaseEmailVerificationLinkService(
        IConfiguration configuration,
        ILogger<FirebaseEmailVerificationLinkService> logger)
    {
        this.logger = logger;
        continueUrl = configuration["EmailVerification:ContinueUrl"] ?? string.Empty;
        var projectId = configuration["Firebase:ProjectId"] ?? "tripmate-82be3";

        try
        {
            if (FirebaseApp.DefaultInstance is null)
            {
                var credentialsPath = configuration["GOOGLE_APPLICATION_CREDENTIALS"]
                    ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
                var credentialsJson = configuration["FIREBASE_SERVICE_ACCOUNT_KEY_JSON"]
                    ?? configuration["Firebase:ServiceAccountKeyJson"];

                GoogleCredential? credential = !string.IsNullOrWhiteSpace(credentialsJson)
                    ? GoogleCredential.FromJson(credentialsJson)
                    : !string.IsNullOrWhiteSpace(credentialsPath) && File.Exists(credentialsPath)
                        ? GoogleCredential.FromFile(credentialsPath)
                        : null;

                credential ??= GoogleCredential.GetApplicationDefault();
                FirebaseApp.Create(new AppOptions { Credential = credential, ProjectId = projectId });
            }

            firebaseAuth = FirebaseAuth.DefaultInstance;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Firebase Admin email verification link generation is unavailable.");
        }
    }

    public async Task<string> GenerateAsync(string email, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(continueUrl, UriKind.Absolute, out _))
        {
            throw new FirebaseUnavailableException("Email verification continue URL is not configured.");
        }

        if (firebaseAuth is null)
        {
            throw new FirebaseUnavailableException("Firebase Admin SDK is not configured.");
        }

        try
        {
            return await firebaseAuth.GenerateEmailVerificationLinkAsync(
                email,
                new ActionCodeSettings { Url = continueUrl, HandleCodeInApp = false },
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Firebase email verification link generation failed.");
            throw new FirebaseUnavailableException("Firebase email verification link generation failed.", exception);
        }
    }

    public async Task<bool> IsVerifiedAsync(string email, CancellationToken cancellationToken)
    {
        if (firebaseAuth is null)
            throw new FirebaseUnavailableException("Firebase Admin SDK is not configured.");

        try
        {
            var user = await firebaseAuth.GetUserByEmailAsync(email, cancellationToken);
            return user.EmailVerified;
        }
        catch (FirebaseAuthException exception) when (exception.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Firebase email verification status lookup failed.");
            throw new FirebaseUnavailableException("Firebase email verification status lookup failed.", exception);
        }
    }
}