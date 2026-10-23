using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Authentication;
using TripMate.Infrastructure.Services;

using Xunit;

namespace TripMate.Infrastructure.UnitTests.Services;

public class StaffJwtAndCurrentUserTests
{
    private static readonly string ValidBase64SigningKey =
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef0123456789abcdef"));

    [Theory]
    [InlineData(UserRole.Traveler, "Traveler")]
    [InlineData(UserRole.TourOperator, "TourOperator")]
    [InlineData(UserRole.Administrator, "Administrator")]
    [InlineData(UserRole.Staff, "Staff")]
    public void GenerateAccessToken_IncludesExactRoleClaim(UserRole role, string expectedClaimRole)
    {
        var jwtOptions = Options.Create(new JwtOptions
        {
            Issuer = "TripMateTestIssuer",
            Audience = "TripMateTestAudience",
            SigningKey = ValidBase64SigningKey,
            AccessTokenLifetimeMinutes = 30,
        });

        var tokenService = new JwtTokenService(jwtOptions);
        var user = new User
        {
            Id = 505,
            Email = "staff@tripmate.vn",
            FullName = "TripMate Staff",
            Role = role,
            Status = AccountStatus.Active,
        };

        var (token, _) = tokenService.GenerateAccessToken(user);
        token.Should().NotBeNullOrWhiteSpace();

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        var roleClaim = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role || c.Type == "role");
        roleClaim.Should().NotBeNull();
        roleClaim!.Value.Should().Be(expectedClaimRole);
    }

    [Theory]
    [InlineData(UserRole.Staff, "Staff")]
    [InlineData(UserRole.Administrator, "Administrator")]
    [InlineData(UserRole.TourOperator, "TourOperator")]
    [InlineData(UserRole.Traveler, "Traveler")]
    public void CurrentUserService_ReturnsAuthenticatedRoleFromClaims(UserRole role, string expectedRole)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "505"),
                new Claim(ClaimTypes.Role, role.ToString()),
            }, "TestAuth")),
        };

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var currentUserService = new CurrentUserService(accessor);

        currentUserService.UserId.Should().Be(505);
        currentUserService.Role.Should().Be(expectedRole);
    }
}