using FluentAssertions;
using Moq;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.GoogleAuth;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.GoogleAuth;

/// <summary>
/// Regression tests for the Google Login account-status gate.
///
/// Security invariant: a successful Google/Firebase token verification proves identity only —
/// it must NOT bypass administrative account-status restrictions. Locked/Inactive accounts must
/// be rejected before any mutation or session-token issuance.
/// </summary>
public class GoogleAuthCommandHandlerTests
{
    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly Mock<IFirebaseAuthService> _firebaseAuthService = new();
    private readonly Mock<IGoogleTokenValidator> _googleTokenValidator = new();
    private readonly Mock<IJwtTokenService> _jwtTokenService = new();
    private readonly Mock<IDateTimeProvider> _dateTimeProvider = new();
    private readonly GoogleAuthCommandHandler _handler;

    public GoogleAuthCommandHandlerTests()
    {
        _dateTimeProvider.Setup(p => p.UtcNow).Returns(DateTimeOffset.UtcNow);
        _jwtTokenService.Setup(j => j.GenerateRefreshToken()).Returns("sample-refresh-token");
        _jwtTokenService.Setup(j => j.HashRefreshToken(It.IsAny<string>())).Returns("sample-hash");
        _jwtTokenService.Setup(j => j.GenerateAccessToken(It.IsAny<User>()))
            .Returns(("sample-access-token", DateTimeOffset.UtcNow.AddHours(1)));

        _handler = new GoogleAuthCommandHandler(
            _dbContext,
            _firebaseAuthService.Object,
            _googleTokenValidator.Object,
            _jwtTokenService.Object,
            _dateTimeProvider.Object);
    }

    private void FirebaseReturnsVerifiedEmail(string email) =>
        _firebaseAuthService
            .Setup(s => s.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FirebaseTokenValidationResult("fb-uid-1", email, true));

    private async Task<User> SeedUser(string email, AccountStatus status, UserRole role = UserRole.Traveler)
    {
        var user = new User
        {
            Email = email,
            FullName = "Test User",
            Role = role,
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    [Theory]
    [InlineData(AccountStatus.Locked, AuthErrorCodes.AccountLocked)]
    [InlineData(AccountStatus.Inactive, AuthErrorCodes.AccountInactive)]
    public async Task Handle_WhenAccountBlockedByStatus_RejectsWithoutIssuingTokensOrMutating(
        AccountStatus status,
        string expectedErrorCode)
    {
        var user = await SeedUser("blocked@example.com", status);
        FirebaseReturnsVerifiedEmail("blocked@example.com");

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(expectedErrorCode);

        // Blocked account must not be mutated.
        var persistedUser = await _dbContext.Users.FindAsync(user.Id);
        persistedUser!.Status.Should().Be(status);

        // Security invariant: no session issued — no refresh-token row, and the JWT service
        // was never invoked for access/refresh token generation.
        _dbContext.RefreshTokens.Should().BeEmpty();
        _jwtTokenService.Verify(j => j.GenerateAccessToken(It.IsAny<User>()), Times.Never);
        _jwtTokenService.Verify(j => j.GenerateRefreshToken(), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenActiveAccount_IssuesSessionTokens()
    {
        var user = await SeedUser("active@example.com", AccountStatus.Active);
        FirebaseReturnsVerifiedEmail("active@example.com");

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("sample-access-token");
        result.Value.RefreshToken.Should().Be("sample-refresh-token");
        _dbContext.RefreshTokens.Should().ContainSingle(t => t.UserId == user.Id);
    }

    [Fact]
    public async Task Handle_WhenPendingEmailVerification_StillActivatesPerCurrentPolicy()
    {
        // Existing behavior, intentionally unchanged by this fix (UC-04 owns broader policy).
        var user = await SeedUser("pending@example.com", AccountStatus.PendingEmailVerification);
        FirebaseReturnsVerifiedEmail("pending@example.com");

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var persistedUser = await _dbContext.Users.FindAsync(user.Id);
        persistedUser!.Status.Should().Be(AccountStatus.Active);
    }
}
