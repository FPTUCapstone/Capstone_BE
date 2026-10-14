using System.Text;

using Microsoft.Extensions.Options;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Authentication;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public enum TestJwtKind
{
    Malformed,
    Expired,
    InvalidSignature,
    InvalidIssuer,
}

internal static class TestJwtTokenFactory
{
    public static string CreateValid(long userId, UserRole role)
    {
        var options = CreateOptions();
        var user = new User
        {
            Id = userId,
            Email = $"jwt-valid-{userId}@example.com",
            FullName = "JWT Valid Case",
            Role = role,
            Status = AccountStatus.Active,
        };

        return new JwtTokenService(Options.Create(options))
            .GenerateAccessToken(user)
            .Token;
    }

    public static string Create(TestJwtKind tokenKind)
    {
        if (tokenKind == TestJwtKind.Malformed)
        {
            return "not-a-jwt";
        }

        var options = CreateOptions();
        options.Issuer = tokenKind == TestJwtKind.InvalidIssuer
            ? "TripMate.Tests.InvalidIssuer"
            : TripMateApiFactory.JwtIssuer;
        options.SigningKey = tokenKind == TestJwtKind.InvalidSignature
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "different-test-only-signing-key-that-is-long-enough"))
            : TripMateApiFactory.JwtSigningKey;
        options.AccessTokenLifetimeMinutes = tokenKind == TestJwtKind.Expired ? -5 : 15;
        var user = new User
        {
            Id = 1,
            Email = "jwt-invalid-case@example.com",
            FullName = "JWT Invalid Case",
            Role = UserRole.Administrator,
            Status = AccountStatus.Active,
        };

        return new JwtTokenService(Options.Create(options))
            .GenerateAccessToken(user)
            .Token;
    }

    private static JwtOptions CreateOptions() => new()
    {
        Issuer = TripMateApiFactory.JwtIssuer,
        Audience = TripMateApiFactory.JwtAudience,
        SigningKey = TripMateApiFactory.JwtSigningKey,
        AccessTokenLifetimeMinutes = 15,
    };
}