using System.Text.Json.Serialization;

using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.GoogleAuth;

/// <summary>
/// G1-A response shape: aligned with the login `AuthResponseDto` plus `isNewAccount`.
/// Role and application state come from current database data; Status is effective eligibility.
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
    bool IsNewAccount)
{
    [JsonIgnore]
    public string? ApplicationStatus { get; init; }

    [JsonIgnore]
    public DateTimeOffset RefreshTokenExpiresAtUtc { get; init; }
}