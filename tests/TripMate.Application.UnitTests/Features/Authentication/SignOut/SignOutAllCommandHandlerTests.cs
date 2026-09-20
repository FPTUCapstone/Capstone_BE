using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.SignOut;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.SignOut;

public class SignOutAllCommandHandlerTests
{
    private readonly FakeJwtTokenService _jwt = new();
    private readonly FakeDateTimeProvider _clock = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WithMissingToken_ReturnsInvalidCredentialWithoutAudit(string? refreshToken)
    {
        await using var db = TestDbContext.Create();

        var result = await CreateHandler(db).Handle(
            new SignOutAllCommand(refreshToken),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthTokenInvalid);
        (await db.AuditLogs.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WithUnknownToken_ReturnsInvalidCredential()
    {
        await using var db = TestDbContext.Create();

        var result = await CreateHandler(db).Handle(
            new SignOutAllCommand("unknown"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthTokenInvalid);
    }

    [Fact]
    public async Task Handle_WithResolvableCredential_RevokesOnlyThatUsersActiveSessions()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com");
        var other = await SeedUser(db, "other@example.com");
        user.Role = UserRole.TourOperator;
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            CompanyName = "TripMate Tours",
            TaxCode = "TAX-001",
            BusinessLicenseNo = "LICENSE-001",
            ContactPhone = "0905123456",
            ContactAddress = "Da Nang",
            CommissionRate = 12.50m,
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = _clock.UtcNow.AddDays(-30),
            UpdatedAtUtc = _clock.UtcNow.AddDays(-2),
        });
        db.RefreshTokens.Add(NewSession(user.Id, "current"));
        db.RefreshTokens.Add(NewSession(user.Id, "second"));
        db.RefreshTokens.Add(NewSession(other.Id, "other-user"));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutAllCommand("current", "Mobile", "trace-all"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.RefreshTokens.CountAsync(
            token => token.UserId == user.Id && token.RevokedAtUtc == null)).Should().Be(0);
        (await db.RefreshTokens.SingleAsync(
            token => token.UserId == other.Id)).RevokedAtUtc.Should().BeNull();

        var audit = await db.AuditLogs.SingleAsync();
        audit.ActionType.Should().Be(AuditActionTypes.AuthSignOutAll);
        audit.ActorUserId.Should().Be(user.Id);
        audit.AfterData.Should().Contain("\"RevokedSessionCount\":2")
            .And.Contain("\"Platform\":\"Mobile\"");
        db.ClearTrackedEntities();
        var persistedUser = await db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        persistedUser.Role.Should().Be(UserRole.TourOperator);
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
        var persistedProfile = await db.OperatorProfiles.SingleAsync(
            profile => profile.UserId == user.Id);
        persistedProfile.CompanyName.Should().Be("TripMate Tours");
        persistedProfile.TaxCode.Should().Be("TAX-001");
        persistedProfile.BusinessLicenseNo.Should().Be("LICENSE-001");
        persistedProfile.ContactPhone.Should().Be("0905123456");
        persistedProfile.ContactAddress.Should().Be("Da Nang");
        persistedProfile.CommissionRate.Should().Be(12.50m);
        persistedProfile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Approved);
        persistedProfile.RejectionReason.Should().BeNull();
        persistedProfile.ReviewedBy.Should().BeNull();
        persistedProfile.ReviewedAtUtc.Should().BeNull();
        persistedProfile.CreatedAtUtc.Should().Be(_clock.UtcNow.AddDays(-30));
        persistedProfile.UpdatedAtUtc.Should().Be(_clock.UtcNow.AddDays(-2));
        db.TransactionExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WithAlreadyRevokedCredential_StillRevokesOtherSessions()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com");
        var firstRevokedAt = _clock.UtcNow.AddMinutes(-10);
        db.RefreshTokens.Add(NewSession(user.Id, "current", firstRevokedAt));
        db.RefreshTokens.Add(NewSession(user.Id, "other"));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutAllCommand("current"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var sessions = await db.RefreshTokens.OrderBy(token => token.Id).ToListAsync();
        sessions[0].RevokedAtUtc.Should().Be(firstRevokedAt);
        sessions[1].RevokedAtUtc.Should().Be(_clock.UtcNow);
    }

    private SignOutAllCommandHandler CreateHandler(TestDbContext db) => new(db, _jwt, _clock);

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