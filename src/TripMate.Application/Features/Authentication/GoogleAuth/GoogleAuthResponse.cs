using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.GoogleAuth;

/// <summary>
/// G1-A response shape: aligned with the login `AuthResponseDto` plus `isNewAccount`.
/// Role/Status are always resolved from the database; enums serialize as strings.
/// </summary>
public sealed record GoogleAuthResponse(
    long UserId,
    string Email,
    string FullName,
    UserRole Role,
    AccountStatus Status,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    bool IsNewAccount);
