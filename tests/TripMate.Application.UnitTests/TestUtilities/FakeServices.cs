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
    public int AccessTokenGenerationCount { get; private set; }
    public int RefreshTokenGenerationCount { get; private set; }

    public Exception? ThrowOnGenerateAccessToken { get; set; }

    public (string Token, DateTimeOffset ExpiresAtUtc) GenerateAccessToken(User user)
    {
        AccessTokenGenerationCount++;
        if (ThrowOnGenerateAccessToken is not null)
        {
            throw ThrowOnGenerateAccessToken;
        }

        return ($"access-token-for-{user.Id}", DateTimeOffset.UtcNow.AddMinutes(15));
    }

    public string GenerateRefreshToken()
    {
        RefreshTokenGenerationCount++;
        return $"refresh-token-{Guid.NewGuid()}";
    }

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

/// <summary>Reversible only within the test process; never a stand-in for real key management.</summary>
public class FakePiiProtector : IPiiProtector
{
    public string Protect(string plaintext) => $"protected:{plaintext}";

    public string Unprotect(string protectedValue) =>
        protectedValue.StartsWith("protected:", StringComparison.Ordinal)
            ? protectedValue["protected:".Length..]
            : throw new FormatException("Value was not protected by FakePiiProtector.");
}

/// <summary>
/// No-op stand-in for IAccountCredentialLock. The real implementation issues sp_getapplock,
/// which the EF InMemory provider cannot execute; the true cross-process serialization
/// guarantee is proven only by the SQL Server integration test.
/// </summary>
public class FakeAccountCredentialLock : IAccountCredentialLock
{
    public int AcquireCallCount { get; private set; }

    public Exception? ThrowOnAcquire { get; set; }

    public Task AcquireAsync(long userId, CancellationToken cancellationToken)
    {
        AcquireCallCount++;
        if (ThrowOnAcquire is not null)
        {
            throw ThrowOnAcquire;
        }

        return Task.CompletedTask;
    }
}