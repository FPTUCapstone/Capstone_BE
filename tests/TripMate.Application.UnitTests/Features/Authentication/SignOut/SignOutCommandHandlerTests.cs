using FluentAssertions;

using TripMate.Application.Features.Authentication.SignOut;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.SignOut;

public class SignOutCommandHandlerTests
{
    private readonly FakeJwtTokenService _jwtTokenService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();

    [Fact]
    public async Task Handle_ActiveMatchingToken_RevokesOnlyMatchingSessionAtCurrentTime()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, UserRole.Traveler);
        var matching = NewSession(user.Id, "matching-token");
        var other = NewSession(user.Id, "other-token");
        db.RefreshTokens.AddRange(matching, other);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutCommand("matching-token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        matching.RevokedAtUtc.Should().Be(_dateTimeProvider.UtcNow);
        other.RevokedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Handle_AlreadyRevokedToken_ReturnsSuccessWithoutChangingTimestamp()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, UserRole.Traveler);
        var originalRevokedAt = _dateTimeProvider.UtcNow.AddHours(-2);
        var session = NewSession(user.Id, "revoked-token");
        session.RevokedAtUtc = originalRevokedAt;
        db.RefreshTokens.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutCommand("revoked-token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        session.RevokedAtUtc.Should().Be(originalRevokedAt);
    }

    [Fact]
    public async Task Handle_UnknownToken_ReturnsSuccessWithoutMutation()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, UserRole.Traveler);
        var session = NewSession(user.Id, "known-token");
        db.RefreshTokens.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutCommand("unknown-token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        session.RevokedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_NullOrWhitespaceToken_ReturnsSuccessWithoutMutation(string? rawToken)
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, UserRole.Traveler);
        var session = NewSession(user.Id, "existing-token");
        db.RefreshTokens.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutCommand(rawToken), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        session.RevokedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ExpiredNonRevokedToken_StillRevokesSession()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, UserRole.Traveler);
        var session = NewSession(user.Id, "expired-token");
        session.ExpiresAtUtc = _dateTimeProvider.UtcNow.AddDays(-1);
        db.RefreshTokens.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutCommand("expired-token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        session.RevokedAtUtc.Should().Be(_dateTimeProvider.UtcNow);
    }

    [Fact]
    public async Task Handle_SignOut_DoesNotMutateUserOrOperatorProfile()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, UserRole.TourOperator);
        var profile = new OperatorProfile
        {
            UserId = user.Id,
            CompanyName = "TripMate Tours",
            TaxCode = "TAX-001",
            BusinessLicenseNo = "BL-001",
            ContactPhone = "0900000000",
            ContactAddress = "Da Nang",
            CommissionRate = 12.5m,
            ApprovalStatus = OperatorApprovalStatus.Approved,
            ReviewedBy = null,
            ReviewedAtUtc = _dateTimeProvider.UtcNow.AddDays(-2),
            CreatedAtUtc = _dateTimeProvider.UtcNow.AddDays(-10),
            UpdatedAtUtc = _dateTimeProvider.UtcNow.AddDays(-2),
        };
        db.OperatorProfiles.Add(profile);
        db.RefreshTokens.Add(NewSession(user.Id, "operator-token"));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new SignOutCommand("operator-token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Role.Should().Be(UserRole.TourOperator);
        user.Status.Should().Be(AccountStatus.Active);
        user.LastLoginAtUtc.Should().Be(_dateTimeProvider.UtcNow.AddHours(-1));
        user.UpdatedAtUtc.Should().Be(_dateTimeProvider.UtcNow.AddDays(-1));
        user.PasswordHash.Should().Be("hashed:password");
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Approved);
        profile.ReviewedBy.Should().BeNull();
        profile.ReviewedAtUtc.Should().Be(_dateTimeProvider.UtcNow.AddDays(-2));
        profile.UpdatedAtUtc.Should().Be(_dateTimeProvider.UtcNow.AddDays(-2));
    }

    private SignOutCommandHandler CreateHandler(TestDbContext dbContext) =>
        new(dbContext, _jwtTokenService, _dateTimeProvider);

    private RefreshToken NewSession(long userId, string rawToken) => new()
    {
        UserId = userId,
        TokenHash = _jwtTokenService.HashRefreshToken(rawToken),
        CreatedAtUtc = _dateTimeProvider.UtcNow.AddDays(-1),
        ExpiresAtUtc = _dateTimeProvider.UtcNow.AddDays(6),
    };

    private async Task<User> SeedUser(TestDbContext db, UserRole role)
    {
        var user = new User
        {
            Email = "user@example.com",
            FullName = "Test User",
            Role = role,
            Status = AccountStatus.Active,
            PasswordHash = "hashed:password",
            EmailVerifiedAtUtc = _dateTimeProvider.UtcNow.AddDays(-5),
            CreatedAtUtc = _dateTimeProvider.UtcNow.AddDays(-10),
            UpdatedAtUtc = _dateTimeProvider.UtcNow.AddDays(-1),
            LastLoginAtUtc = _dateTimeProvider.UtcNow.AddHours(-1),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}