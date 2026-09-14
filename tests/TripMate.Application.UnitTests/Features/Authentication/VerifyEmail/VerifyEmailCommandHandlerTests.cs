using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.VerifyEmail;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.VerifyEmail;

public class VerifyEmailCommandHandlerTests
{
    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly Mock<IFirebaseAuthService> _firebaseAuthService = new();
    private readonly Mock<IJwtTokenService> _jwtTokenService = new();
    private readonly Mock<IDateTimeProvider> _dateTimeProvider = new();
    private readonly VerifyEmailCommandHandler _handler;

    public VerifyEmailCommandHandlerTests()
    {
        _dateTimeProvider.Setup(p => p.UtcNow).Returns(DateTimeOffset.UtcNow);
        _jwtTokenService.Setup(j => j.GenerateRefreshToken()).Returns("sample-refresh-token");
        _jwtTokenService.Setup(j => j.HashRefreshToken(It.IsAny<string>())).Returns("sample-hash");
        _jwtTokenService.Setup(j => j.GenerateAccessToken(It.IsAny<User>()))
            .Returns(("sample-access-token", DateTimeOffset.UtcNow.AddHours(1)));

        _handler = new VerifyEmailCommandHandler(
            _dbContext,
            _firebaseAuthService.Object,
            _jwtTokenService.Object,
            _dateTimeProvider.Object,
            NullLogger<VerifyEmailCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenEmailNotVerifiedInFirebase_ReturnsError()
    {
        _firebaseAuthService.Setup(s => s.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FirebaseTokenValidationResult("fb-uid-1", "user@example.com", false));

        var command = new VerifyEmailCommand("token-unverified");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("MSG_EMAIL_NOT_VERIFIED");
    }

    [Fact]
    public async Task Handle_WhenEmailVerified_ActivatesUserAndIssuesJwt()
    {
        var user = new User
        {
            Email = "user@example.com",
            FullName = "Test Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.PendingEmailVerification,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        _firebaseAuthService.Setup(s => s.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FirebaseTokenValidationResult("fb-uid-1", "user@example.com", true));

        var command = new VerifyEmailCommand("token-verified");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(AccountStatus.Active.ToString());
        result.Value.AccessToken.Should().Be("sample-access-token");
        result.Value.RefreshToken.Should().Be("sample-refresh-token");

        var updatedUser = await _dbContext.Users.FindAsync(user.Id);
        updatedUser!.Status.Should().Be(AccountStatus.Active);
        updatedUser.EmailVerifiedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_WhenTokenVerificationThrows_ReturnsGenericMessageWithoutExceptionDetails()
    {
        _firebaseAuthService
            .Setup(s => s.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "Firebase Admin SDK is not configured; internal connection string Password=secret"));

        var command = new VerifyEmailCommand("bad-token");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg14);
        result.ErrorMessage.Should().Be("Invalid or expired Firebase authentication token.");
        result.ErrorMessage.Should().NotContain("Password=secret");
        result.ErrorMessage.Should().NotContain("Firebase Admin SDK");
    }

    // SEC (Verify Email account-status fix): a verified Firebase token proves email ownership —
    // it must NOT override an administrative account state. Only PendingEmailVerification may be
    // activated; Locked/Inactive accounts must be rejected with the established status codes and
    // receive no session tokens.
    [Theory]
    [InlineData(AccountStatus.Locked, AuthErrorCodes.AccountLocked)]
    [InlineData(AccountStatus.Inactive, AuthErrorCodes.AccountInactive)]
    public async Task Handle_WhenAccountInBlockedStatus_RejectsWithoutActivatingOrIssuingTokens(
        AccountStatus status,
        string expectedErrorCode)
    {
        var user = new User
        {
            Email = "blocked@example.com",
            FullName = "Blocked User",
            Role = UserRole.Traveler,
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        _firebaseAuthService
            .Setup(s => s.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FirebaseTokenValidationResult("fb-uid-1", "blocked@example.com", true));

        var command = new VerifyEmailCommand("token-verified-but-account-blocked");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(expectedErrorCode);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        // Status must be unchanged (not auto-activated) and no refresh token persisted.
        var persistedUser = await _dbContext.Users.FindAsync(user.Id);
        persistedUser!.Status.Should().Be(status);
        persistedUser.EmailVerifiedAtUtc.Should().BeNull();
        _dbContext.RefreshTokens.Should().BeEmpty();
    }
}
