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
);
