using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Authentication.SignOut;
using TripMate.Application.Features.Authentication.WebSignOut;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.WebSignOut;

public class WebSignOutCommandHandlerTests
{
    [Fact]
    public async Task Handle_UsesWebAuditContextAndRevokesCurrentSession()
    {
        await using var db = TestDbContext.Create();
        var jwt = new FakeJwtTokenService();
        var clock = new FakeDateTimeProvider();
        var user = await SeedUser(db, clock);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = jwt.HashRefreshToken("web-token"),
            CreatedAtUtc = clock.UtcNow,
            ExpiresAtUtc = clock.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();

        var inner = new SignOutCommandHandler(db, jwt, clock);
        var handler = new WebSignOutCommandHandler(inner);

        var result = await handler.Handle(
            new WebSignOutCommand("web-token", "trace-web", "127.0.0.1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.RefreshTokens.SingleAsync()).RevokedAtUtc.Should().Be(clock.UtcNow);
        (await db.AuditLogs.SingleAsync()).AfterData.Should().Contain("\"Platform\":\"Web\"")
            .And.Contain("\"TraceId\":\"trace-web\"");
    }

    [Fact]
    public async Task Handle_SecondRequest_DoesNotOverwriteFirstRevocationTimestamp()
    {
        await using var db = TestDbContext.Create();
        var jwt = new FakeJwtTokenService();
        var clock = new FakeDateTimeProvider();
        var user = await SeedUser(db, clock);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = jwt.HashRefreshToken("web-token"),
            CreatedAtUtc = clock.UtcNow,
            ExpiresAtUtc = clock.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();

        var handler = new WebSignOutCommandHandler(new SignOutCommandHandler(db, jwt, clock));
        await handler.Handle(new WebSignOutCommand("web-token"), CancellationToken.None);
        var firstRevokedAt = (await db.RefreshTokens.SingleAsync()).RevokedAtUtc;

        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        await handler.Handle(new WebSignOutCommand("web-token"), CancellationToken.None);

        (await db.RefreshTokens.SingleAsync()).RevokedAtUtc.Should().Be(firstRevokedAt);
        (await db.AuditLogs.CountAsync()).Should().Be(1,
            "only the request that performed the revocation receives a security audit event");
    }

    private static async Task<User> SeedUser(TestDbContext db, FakeDateTimeProvider clock)
    {
        var user = new User
        {
            Email = "user@example.com",
            FullName = "Test User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            PasswordHash = "hashed:x",
            CreatedAtUtc = clock.UtcNow,
            UpdatedAtUtc = clock.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}