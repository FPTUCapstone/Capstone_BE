using FluentAssertions;

using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.WebRefresh;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.WebRefresh;

/// <summary>
/// S01 session restoration: the HttpOnly refresh cookie is redeemed for the
/// authoritative current context. Non-rotating model: the refresh row and its
/// original expiry are never modified, and no new refresh credential is issued.
/// </summary>
public class WebRefreshCommandHandlerTests
{
    private readonly FakeJwtTokenService _jwtTokenService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();

    [Fact]
    public async Task Handle_WithValidActiveSession_ReturnsCurrentContextWithoutRotation()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com", UserRole.Traveler);
        var raw = "valid-refresh-token";
        db.RefreshTokens.Add(NewSession(user.Id, raw));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new WebRefreshCommand(raw), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(user.Id);
        result.Value.Email.Should().Be("user@example.com");
        result.Value.Role.Should().Be(UserRole.Traveler);
        result.Value.Status.Should().Be(AccountStatus.Active);
        result.Value.ApplicationStatus.Should().BeNull();
        result.Value.AccessToken.Should().NotBeNullOrWhiteSpace();
        // Non-rotating: exactly the original row remains, unchanged expiry, no new credential.
        var row = db.RefreshTokens.Should().ContainSingle().Subject;
        row.TokenHash.Should().Be(_jwtTokenService.HashRefreshToken(raw));
        row.ExpiresAtUtc.Should().Be(_dateTimeProvider.UtcNow.AddDays(7));
        // Refresh is not login: no last-login stamp, no status mutation.
        (await db.Users.FindAsync(user.Id))!.LastLoginAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(UserRole.Traveler, null, null)]
    [InlineData(UserRole.Administrator, null, null)]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.Approved, "Approved")]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.PendingApproval, "PendingApproval")]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.Rejected, "Rejected")]
    [InlineData(UserRole.TourOperator, null, null)]
    [InlineData(UserRole.TourOperator, (OperatorApprovalStatus)99, null)]
    public async Task Handle_ReturnsAuthoritativeApplicationContext(UserRole role, OperatorApprovalStatus? approval, string? expected)
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "ctx@example.com", role);
        if (approval.HasValue) db.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = approval.Value });
        var raw = "ctx-refresh";
        db.RefreshTokens.Add(NewSession(user.Id, raw));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new WebRefreshCommand(raw), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(role);
        result.Value.Status.Should().Be(AccountStatus.Active);
        result.Value.ApplicationStatus.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown-token-value")]
    public async Task Handle_WithMissingOrUnknownToken_FailsWithInvalidSession(string? raw)
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com", UserRole.Traveler);
        db.RefreshTokens.Add(NewSession(user.Id, "some-other-token"));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new WebRefreshCommand(raw), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthTokenInvalid);
    }

    [Fact]
    public async Task Handle_WithExpiredSession_FailsWithInvalidSession()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com", UserRole.Traveler);
        var raw = "expired-refresh";
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = _jwtTokenService.HashRefreshToken(raw),
            CreatedAtUtc = _dateTimeProvider.UtcNow.AddDays(-8),
            ExpiresAtUtc = _dateTimeProvider.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new WebRefreshCommand(raw), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthTokenInvalid);
    }

    [Fact]
    public async Task Handle_WithRevokedSession_FailsWithInvalidSession()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com", UserRole.Traveler);
        var raw = "revoked-refresh";
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = _jwtTokenService.HashRefreshToken(raw),
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            ExpiresAtUtc = _dateTimeProvider.UtcNow.AddDays(7),
            RevokedAtUtc = _dateTimeProvider.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new WebRefreshCommand(raw), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthTokenInvalid);
    }

    [Theory]
    [InlineData(AccountStatus.Locked, AuthErrorCodes.AccountLocked)]
    [InlineData(AccountStatus.Inactive, AuthErrorCodes.AccountInactive)]
    [InlineData(AccountStatus.PendingEmailVerification, AuthErrorCodes.AccountPendingVerification)]
    public async Task Handle_WithBlockedAccount_ReusesLoginEligibilityGates(AccountStatus status, string expectedCode)
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "user@example.com", UserRole.Traveler, status);
        var raw = "blocked-refresh";
        db.RefreshTokens.Add(NewSession(user.Id, raw));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new WebRefreshCommand(raw), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedCode);
    }

    [Fact]
    public async Task Handle_WithLegacyRejectedOperatorAndMatchingProfile_ReturnsEffectiveActiveWithoutMutation()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "legacy@example.com", UserRole.TourOperator, AccountStatus.Rejected);
        user.EmailVerifiedAtUtc = user.CreatedAtUtc;
        db.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = OperatorApprovalStatus.Rejected });
        var raw = "legacy-refresh";
        db.RefreshTokens.Add(NewSession(user.Id, raw));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new WebRefreshCommand(raw), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(AccountStatus.Active);
        result.Value.ApplicationStatus.Should().Be("Rejected");
        (await db.Users.FindAsync(user.Id))!.Status.Should().Be(AccountStatus.Rejected);
    }

    private WebRefreshCommandHandler CreateHandler(TestDbContext dbContext) =>
        new(dbContext, _jwtTokenService, _dateTimeProvider);

    private RefreshToken NewSession(long userId, string raw) => new()
    {
        UserId = userId,
        TokenHash = _jwtTokenService.HashRefreshToken(raw),
        CreatedAtUtc = _dateTimeProvider.UtcNow,
        ExpiresAtUtc = _dateTimeProvider.UtcNow.AddDays(7),
    };

    private async Task<User> SeedUser(TestDbContext db, string email, UserRole role, AccountStatus status = AccountStatus.Active)
    {
        var user = new User
        {
            Email = email,
            FullName = "Test User",
            Role = role,
            Status = status,
            PasswordHash = "hashed:x",
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            UpdatedAtUtc = _dateTimeProvider.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}