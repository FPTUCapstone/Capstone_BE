using TripMate.Domain.Entities;

namespace TripMate.Application.Common.Interfaces;

public interface IJwtTokenService
{
    (string Token, DateTimeOffset ExpiresAtUtc) GenerateAccessToken(User user);

    string GenerateRefreshToken();

    /// <summary>
    /// Hashes an opaque refresh token before it is persisted (dbo.RefreshTokens.token_hash).
    /// Not the same tool as password hashing: a refresh token is already high-entropy random
    /// data, so a fast general-purpose hash is correct here — Argon2id's deliberate slowness
    /// solves a different problem (low-entropy secrets) and would be the wrong tool.
    /// </summary>
    string HashRefreshToken(string rawRefreshToken);
}
