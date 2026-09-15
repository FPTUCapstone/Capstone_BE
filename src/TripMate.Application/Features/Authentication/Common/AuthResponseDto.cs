using System.Text.Json.Serialization;

using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.Common;

public record AuthResponseDto(
    long UserId,
    string? Email,
    string FullName,
    UserRole Role,
    AccountStatus Status,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAtUtc
)
{
    // Internal context for Web projection; preserve the existing Mobile wire contract.
    [JsonIgnore]
    public string? ApplicationStatus { get; init; }

    [JsonIgnore]
    public DateTimeOffset RefreshTokenExpiresAtUtc { get; init; }
}