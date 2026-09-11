using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.TestUtilities;

public class FakePasswordHasher : IPasswordHasher, IPasswordHasherService
{
    public string Hash(string password) => $"hashed:{password}";

    public bool Verify(string password, string passwordHash) => passwordHash == Hash(password);
}

public class FakeJwtTokenService : IJwtTokenService
{
    public (string Token, DateTimeOffset ExpiresAtUtc) GenerateAccessToken(User user) =>
        ($"access-token-for-{user.Id}", DateTimeOffset.UtcNow.AddMinutes(15));

    public string GenerateRefreshToken() => $"refresh-token-{Guid.NewGuid()}";

    public string HashRefreshToken(string rawRefreshToken) => $"hashed:{rawRefreshToken}";
}

public class FakeDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

public class FakeCurrentUserService : ICurrentUserService
{
    public long? UserId { get; set; }

    public string? Role { get; set; }
}

