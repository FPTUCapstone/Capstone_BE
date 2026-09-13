using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
/// Regression tests for the Google Sign In flow (UC-04 spec v2.0).
///
/// Security invariants: a verified Firebase token proves identity only; provider and email-
/// verification claims fail closed (BR-11/BR-12); administrative status gates apply before any
/// mutation (BR-05/07/08); a Rejected operator may still sign in (BR-06); Sign In never changes
/// the status of an existing account (BR-14); there is no raw-Google fallback (D2-A); Firebase
/// unavailability is a distinct 503-mapped failure (BR-16).
/// </summary>
public class GoogleAuthCommandHandlerTests
{
    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly Mock<IFirebaseAuthService> _firebaseAuthService = new();
    private readonly Mock<IJwtTokenService> _jwtTokenService = new();
    private readonly Mock<IDateTimeProvider> _dateTimeProvider = new();
    private readonly DateTimeOffset _accessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);
    private readonly GoogleAuthCommandHandler _handler;

    public GoogleAuthCommandHandlerTests()
    {
        _dateTimeProvider.Setup(p => p.UtcNow).Returns(DateTimeOffset.UtcNow);
        _jwtTokenService.Setup(j => j.GenerateRefreshToken()).Returns("sample-refresh-token");
        _jwtTokenService.Setup(j => j.HashRefreshToken(It.IsAny<string>())).Returns("sample-hash");
        _jwtTokenService.Setup(j => j.GenerateAccessToken(It.IsAny<User>()))
            .Returns(("sample-access-token", _accessTokenExpiresAtUtc));

        _handler = new GoogleAuthCommandHandler(
            _dbContext,
            _firebaseAuthService.Object,
            _jwtTokenService.Object,
            _dateTimeProvider.Object);
    }

    private void FirebaseReturns(FirebaseTokenValidationResult result) =>
        _firebaseAuthService
            .Setup(s => s.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void FirebaseReturnsVerifiedEmail(string email) =>
        FirebaseReturns(new FirebaseTokenValidationResult("fb-uid-1", email, true, null, null, "google.com"));

    private void FirebaseThrows(Exception exception) =>
        _firebaseAuthService
            .Setup(s => s.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

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
    [InlineData(AccountStatus.Locked, AuthErrorCodes.AccountLocked, "Account is locked.")]
    [InlineData(AccountStatus.Inactive, AuthErrorCodes.AccountInactive, "Account is inactive.")]
    [InlineData(AccountStatus.PendingApproval, AuthErrorCodes.AccountPendingApproval, "Account is pending approval.")]
    public async Task Handle_WhenAccountBlockedByStatus_RejectsWithoutIssuingTokensOrMutating(
        AccountStatus status,
        string expectedErrorCode,
        string expectedMessage)
    {
        var user = await SeedUser("blocked@example.com", status);
        FirebaseReturnsVerifiedEmail("blocked@example.com");

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(expectedErrorCode);
        result.ErrorMessage.Should().Be(expectedMessage);

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
    public async Task Handle_WhenActiveAccount_IssuesSessionTokensWithFullG1AShape()
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

        // G1-A: the Google response carries the same identity fields as the login response.
        result.Value.UserId.Should().Be(user.Id);
        result.Value.Email.Should().Be("active@example.com");
        result.Value.FullName.Should().Be("Test User");
        result.Value.Role.Should().Be(UserRole.Traveler);
        result.Value.Status.Should().Be(AccountStatus.Active);
        result.Value.AccessTokenExpiresAtUtc.Should().Be(_accessTokenExpiresAtUtc);
        result.Value.IsNewAccount.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WhenExistingAdministrator_SignsInWithGoogle_RoleResolvedFromDatabase()
    {
        // BR-18 (D3-A): an existing Administrator account may sign in with Google — the role
        // always comes from the database; Google never grants or changes a role, and Sign In
        // never changes the account status (BR-14). Contrast with BR-02: Google can only ever
        // CREATE Traveler accounts, never an Administrator.
        var user = await SeedUser("admin@example.com", AccountStatus.Active, UserRole.Administrator);
        FirebaseReturnsVerifiedEmail("admin@example.com");

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(UserRole.Administrator);
        result.Value.Status.Should().Be(AccountStatus.Active);
        result.Value.IsNewAccount.Should().BeFalse();
        result.Value.UserId.Should().Be(user.Id);

        var persistedUser = await _dbContext.Users.FindAsync(user.Id);
        persistedUser!.Role.Should().Be(UserRole.Administrator);
        persistedUser.Status.Should().Be(AccountStatus.Active);
    }

    [Fact]
    public async Task Handle_WhenEmailUnknown_AutoProvisionsTravelerActiveWithFullShape()
    {
        // BR-02: creation of a previously nonexistent account — Traveler, Active, never
        // TourOperator/Administrator; identity fields come from the verified Firebase token.
        FirebaseReturns(new FirebaseTokenValidationResult(
            "fb-uid-new", "Lan.Pham@Gmail.com", true, "Lan Pham", "https://photo/lan.png", "google.com"));

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsNewAccount.Should().BeTrue();
        result.Value.Email.Should().Be("lan.pham@gmail.com");
        result.Value.FullName.Should().Be("Lan Pham");
        result.Value.Role.Should().Be(UserRole.Traveler);
        result.Value.Status.Should().Be(AccountStatus.Active);

        var provisioned = await _dbContext.Users.SingleAsync(u => u.Email == "lan.pham@gmail.com");
        provisioned.Role.Should().Be(UserRole.Traveler);
        provisioned.Status.Should().Be(AccountStatus.Active);
        provisioned.AvatarUrl.Should().Be("https://photo/lan.png");
        provisioned.EmailVerifiedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_WhenPendingEmailVerification_BlockedWithoutStatusChange()
    {
        // UC-04 v2.0 BR-03/BR-14 (deliberate change from the previous auto-activation policy):
        // Sign In is not activation — a pending account is rejected and stays untouched.
        var user = await SeedUser("pending@example.com", AccountStatus.PendingEmailVerification);
        FirebaseReturnsVerifiedEmail("pending@example.com");

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AccountPendingVerification);
        result.ErrorMessage.Should().Be("Email has not been verified.");

        var persistedUser = await _dbContext.Users.FindAsync(user.Id);
        persistedUser!.Status.Should().Be(AccountStatus.PendingEmailVerification);
        _dbContext.RefreshTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenRejectedTourOperator_StillSignsInWithoutStatusChange()
    {
        var user = await SeedUser("rejected@example.com", AccountStatus.Rejected, UserRole.TourOperator);
        FirebaseReturnsVerifiedEmail("rejected@example.com");

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var persistedUser = await _dbContext.Users.FindAsync(user.Id);
        persistedUser!.Status.Should().Be(AccountStatus.Rejected);
    }

    [Fact]
    public async Task Handle_WhenFirebaseUnavailable_FailsWithUnavailableCode()
    {
        // BR-16: infrastructure failure is distinct from a token rejection and must never be
        // papered over by a fallback — the raw-Google validator no longer exists (D2-A).
        FirebaseThrows(new FirebaseUnavailableException("Firebase ID token verification is temporarily unavailable."));

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.FirebaseUnavailable);
        result.ErrorMessage.Should().Be("Google authentication service is temporarily unavailable.");
    }

    [Fact]
    public async Task Handle_WhenFirebaseRejectsToken_DoesNotFallBackToRawGoogleValidation()
    {
        FirebaseThrows(new Exception("verification rejected"));

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthTokenInvalid);
        result.ErrorMessage.Should().Be("Invalid Google ID token.");
    }

    [Fact]
    public async Task Handle_WithNonGoogleProvider_FailsClosed()
    {
        // BR-11: only google.com tokens may enter the Google flow.
        FirebaseReturns(new FirebaseTokenValidationResult("fb-uid-1", "user@example.com", true, null, null, "password"));

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-firebase-id-token"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthTokenInvalid);
        result.ErrorMessage.Should().Be("Invalid Google ID token.");
    }

    [Fact]
    public async Task Handle_WithMissingProviderClaim_FailsClosed()
    {
        FirebaseReturns(new FirebaseTokenValidationResult("fb-uid-1", "user@example.com", true));

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-firebase-id-token"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthTokenInvalid);
    }

    [Fact]
    public async Task Handle_WithUnverifiedGoogleEmail_Rejects()
    {
        // BR-12: Google must have verified the email — fail closed on false.
        FirebaseReturns(new FirebaseTokenValidationResult("fb-uid-1", "user@example.com", false, null, null, "google.com"));

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.MsgEmailNotVerified);
        result.ErrorMessage.Should().Be("Google account email has not been verified.");
    }

    [Fact]
    public async Task Handle_WithEmptyTokenEmail_FailsClosed()
    {
        FirebaseReturns(new FirebaseTokenValidationResult("fb-uid-1", string.Empty, true, null, null, "google.com"));

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthTokenInvalid);
        result.ErrorMessage.Should().Be("Invalid Google ID token.");
    }
}
