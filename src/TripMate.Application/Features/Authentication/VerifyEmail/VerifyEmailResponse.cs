namespace TripMate.Application.Features.Authentication.VerifyEmail;

public sealed record VerifyEmailResponse(
    long UserId,
    string Email,
    string Status,
    DateTimeOffset EmailVerifiedAtUtc,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAtUtc);
