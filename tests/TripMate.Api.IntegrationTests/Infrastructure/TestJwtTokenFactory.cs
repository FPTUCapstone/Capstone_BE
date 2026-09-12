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
    public static string Create(TestJwtKind tokenKind)
    {
        if (tokenKind == TestJwtKind.Malformed)
        {
            return "not-a-jwt";
        }

        var options = new JwtOptions
        {
            Issuer = tokenKind == TestJwtKind.InvalidIssuer
                ? "TripMate.Tests.InvalidIssuer"
                : TripMateApiFactory.JwtIssuer,
            Audience = TripMateApiFactory.JwtAudience,
            SigningKey = tokenKind == TestJwtKind.InvalidSignature
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "different-test-only-signing-key-that-is-long-enough"))
                : TripMateApiFactory.JwtSigningKey,
            AccessTokenLifetimeMinutes = tokenKind == TestJwtKind.Expired ? -5 : 15,
        };
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
}