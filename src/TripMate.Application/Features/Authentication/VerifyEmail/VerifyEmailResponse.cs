using System.Text.Json.Serialization;

namespace TripMate.Application.Features.Authentication.VerifyEmail;

/// <summary>
/// Legacy/Mobile verify-email session response. A2 adds the routing identity
/// (role + effective status + applicationStatus) so Mobile can route after
/// verification without a second login. Status is the backend-effective
/// eligibility status (shared resolver), never a raw DB marker; applicationStatus
/// follows the same semantics as login (null for non-operator/unresolved).
/// </summary>
public sealed record VerifyEmailResponse(
    long UserId,
    string Email,
    string Status,
    DateTimeOffset EmailVerifiedAtUtc,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAtUtc)
{
    public required string Role { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ApplicationStatus { get; init; }
}