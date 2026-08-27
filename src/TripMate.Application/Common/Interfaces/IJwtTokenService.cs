using TripMate.Domain.Entities;

namespace TripMate.Application.Common.Interfaces;

public interface IJwtTokenService
{
    (string Token, DateTimeOffset ExpiresAtUtc) GenerateAccessToken(User user);

    string GenerateRefreshToken();
}
