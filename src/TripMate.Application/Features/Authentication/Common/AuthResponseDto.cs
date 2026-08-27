using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.Common;

public record AuthResponseDto(
    Guid UserId,
    string Email,
    string FullName,
    UserRole Role,
    AccountStatus Status,
    TourOperatorApplicationStatus TourOperatorApplicationStatus,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAtUtc
);
