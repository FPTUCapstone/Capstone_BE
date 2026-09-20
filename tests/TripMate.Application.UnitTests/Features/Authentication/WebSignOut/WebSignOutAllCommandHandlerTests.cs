using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Authentication.SignOut;
using TripMate.Application.Features.Authentication.WebSignOut;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.WebSignOut;

public class WebSignOutAllCommandHandlerTests
{
    [Fact]
    public async Task Handle_UsesWebAuditContextAndRevokesAllSessions()
    {
        await using var db = TestDbContext.Create();
        var jwt = new FakeJwtTokenService();
        var clock = new FakeDateTimeProvider();
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
        db.RefreshTokens.AddRange(
            NewSession(user.Id, "current", jwt, clock),
            NewSession(user.Id, "other", jwt, clock));
        await db.SaveChangesAsync();

        var inner = new SignOutAllCommandHandler(db, jwt, clock);
        var handler = new WebSignOutAllCommandHandler(inner);

        var result = await handler.Handle(
            new WebSignOutAllCommand("current", "trace-web-all", "127.0.0.1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.RefreshTokens.CountAsync(token => token.RevokedAtUtc == null)).Should().Be(0);
        (await db.AuditLogs.SingleAsync()).AfterData.Should().Contain("\"Platform\":\"Web\"")
            .And.Contain("\"TraceId\":\"trace-web-all\"");
    }

    private static RefreshToken NewSession(
        long userId,
        string rawToken,
        FakeJwtTokenService jwt,
        FakeDateTimeProvider clock) => new()
        {
            UserId = userId,
            TokenHash = jwt.HashRefreshToken(rawToken),
            CreatedAtUtc = clock.UtcNow,
            ExpiresAtUtc = clock.UtcNow.AddDays(7),
        };
}