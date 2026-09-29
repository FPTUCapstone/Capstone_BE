using System.Text.Json.Serialization;

using TripMate.Application.Features.Authentication.GoogleAuth;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.Common;

/// <summary>Web authentication context. Refresh credentials are delivered separately by cookie.</summary>
public record WebAuthResponseDto(
    long UserId,
    string Email,
    string FullName,
    UserRole Role,
    AccountStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ApplicationStatus,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc)
{
    public static WebAuthResponseDto From(AuthResponseDto response) => new(
        response.UserId, response.Email ?? string.Empty, response.FullName ?? string.Empty,
        response.Role, response.Status, response.ApplicationStatus,
        response.AccessToken, response.AccessTokenExpiresAtUtc);
}

public sealed record WebGoogleAuthResponseDto(
    long UserId,
    string Email,
    string FullName,
    UserRole Role,
    AccountStatus Status,
    string? ApplicationStatus,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    bool IsNewAccount)
    : WebAuthResponseDto(UserId, Email, FullName, Role, Status, ApplicationStatus, AccessToken, AccessTokenExpiresAtUtc)
{
    public static WebGoogleAuthResponseDto From(GoogleAuthResponse response) => new(
        response.UserId, response.Email, response.FullName ?? string.Empty,
        response.Role, response.Status, response.ApplicationStatus,
        response.AccessToken, response.AccessTokenExpiresAtUtc, response.IsNewAccount);
}