namespace TripMate.Application.Common.Interfaces;

public sealed record FirebaseTokenValidationResult(
    string Uid,
    string Email,
    bool EmailVerified,
    string? FullName = null,
    string? Picture = null,
    string? SignInProvider = null
);

/// <summary>
/// Thrown when Firebase ID token verification cannot be performed at all — the Firebase Admin
/// SDK is not configured or the verification infrastructure is unreachable. Distinct from a
/// token rejection: an unavailable verifier must fail closed with 503 (UC-04 BR-16), never 401.
/// </summary>
public class FirebaseUnavailableException : Exception
{
    public FirebaseUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public interface IFirebaseAuthService
{
    Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(string idToken, CancellationToken cancellationToken = default);
}