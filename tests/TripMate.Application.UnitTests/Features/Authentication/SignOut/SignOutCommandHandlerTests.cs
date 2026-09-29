using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Authentication.SignOut;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.SignOut;

public class SignOutCommandHandlerTests
{
    private readonly FakeJwtTokenService _jwt = new();
    private readonly FakeDateTimeProvider _clock = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WithMissingToken_ReturnsSuccessWithoutAudit(string? refreshToken)
    {
        await using var db = TestDbContext.Create();

        var result = await CreateHandler(db).Handle(
            new SignOutCommand(refreshToken, "Mobile", "trace-1", "127.0.0.1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.AuditLogs.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WithUnknownToken_ReturnsSuccessAndDoesNotAffectSessions()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com");
        db.RefreshTokens.Add(NewSession(user.Id, "other-token"));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutCommand("unknown-token"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.RefreshTokens.SingleAsync()).RevokedAtUtc.Should().BeNull();
        (await db.AuditLogs.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WithAlreadyRevokedToken_PreservesFirstTimestamp()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com");
        var firstRevokedAt = _clock.UtcNow.AddMinutes(-5);
        db.RefreshTokens.Add(NewSession(user.Id, "revoked-token", firstRevokedAt));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutCommand("revoked-token"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.RefreshTokens.SingleAsync()).RevokedAtUtc.Should().Be(firstRevokedAt);
        (await db.AuditLogs.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WithActiveToken_AtomicallyRevokesAndAuditsWithoutSecrets()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com");
        const string rawToken = "top-secret-refresh-token";
        db.RefreshTokens.Add(NewSession(user.Id, rawToken));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutCommand(rawToken, "Mobile", "trace-2", "127.0.0.1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.RefreshTokens.SingleAsync()).RevokedAtUtc.Should().Be(_clock.UtcNow);
        var audit = await db.AuditLogs.SingleAsync();
        audit.ActorUserId.Should().Be(user.Id);
        audit.AfterData.Should().Contain("\"Result\":\"Success\"")
            .And.NotContain(rawToken)
            .And.NotContain(_jwt.HashRefreshToken(rawToken));
        db.ClearTrackedEntities();
        var persistedUser = await db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        persistedUser.Role.Should().Be(UserRole.Traveler);
        persistedUser.Email.Should().Be("user@example.com");
        persistedUser.PhoneNumber.Should().Be("0905123456");
        persistedUser.PasswordHash.Should().Be("hashed:x");
        persistedUser.EmailVerifiedAtUtc.Should().Be(_clock.UtcNow.AddDays(-3));
        persistedUser.PhoneVerifiedAtUtc.Should().Be(_clock.UtcNow.AddDays(-2));
        persistedUser.FullName.Should().Be("Test User");
        persistedUser.AvatarUrl.Should().Be("https://example.com/avatar.png");
        persistedUser.Status.Should().Be(AccountStatus.Active);
        persistedUser.CreatedAtUtc.Should().Be(_clock.UtcNow.AddDays(-30));
        persistedUser.UpdatedAtUtc.Should().Be(_clock.UtcNow.AddDays(-1));
        persistedUser.LastLoginAtUtc.Should().Be(_clock.UtcNow.AddHours(-2));
        db.TransactionExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_OnlyRevokesCurrentSession()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com");
        db.RefreshTokens.Add(NewSession(user.Id, "current"));
        db.RefreshTokens.Add(NewSession(user.Id, "other"));
        await db.SaveChangesAsync();

        await CreateHandler(db).Handle(new SignOutCommand("current"), CancellationToken.None);

        var other = await db.RefreshTokens.SingleAsync(
            token => token.TokenHash == _jwt.HashRefreshToken("other"));
        other.RevokedAtUtc.Should().BeNull();
    }

    private SignOutCommandHandler CreateHandler(TestDbContext db) => new(db, _jwt, _clock);

    private RefreshToken NewSession(long userId, string rawToken, DateTimeOffset? revokedAt = null) => new()
    {
        UserId = userId,
        TokenHash = _jwt.HashRefreshToken(rawToken),
        CreatedAtUtc = _clock.UtcNow,
        ExpiresAtUtc = _clock.UtcNow.AddDays(7),
        RevokedAtUtc = revokedAt,
    };

    private async Task<User> SeedUser(TestDbContext db, string email)
    {
        var user = new User
        {
            Email = email,
            FullName = "Test User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            PasswordHash = "hashed:x",
            PhoneNumber = "0905123456",
            EmailVerifiedAtUtc = _clock.UtcNow.AddDays(-3),
            PhoneVerifiedAtUtc = _clock.UtcNow.AddDays(-2),
            AvatarUrl = "https://example.com/avatar.png",
            CreatedAtUtc = _clock.UtcNow.AddDays(-30),
            UpdatedAtUtc = _clock.UtcNow.AddDays(-1),
            LastLoginAtUtc = _clock.UtcNow.AddHours(-2),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}