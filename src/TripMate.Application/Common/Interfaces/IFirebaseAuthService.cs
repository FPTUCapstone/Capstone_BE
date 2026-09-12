namespace TripMate.Application.Common.Interfaces;

public sealed record FirebaseTokenValidationResult(
    string Uid,
    string Email,
    bool EmailVerified,
    string? FullName = null,
    string? Picture = null
);

public interface IFirebaseAuthService
{
    Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(string idToken, CancellationToken cancellationToken = default);
}
