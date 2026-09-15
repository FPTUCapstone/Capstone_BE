using System.Text.Json;
using System.Text.Json.Serialization;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
/// Regression tests for Google Sign In and approved legacy compatibility (UC-04 revision 3).
///
/// Security invariants: a verified Firebase token proves identity only; provider and email-
/// verification claims fail closed (BR-11/BR-12); administrative status gates apply before any
/// mutation; legacy operators require compatible profile and persisted evidence; Sign In never changes
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
            _dateTimeProvider.Object,
            NullLogger<GoogleAuthCommandHandler>.Instance);
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
    [InlineData(AccountStatus.PendingApproval, AuthErrorCodes.AccountStateUnresolved, "Account state could not be resolved.")]
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

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task Handle_WhenActiveAccount_IssuesSessionTokensWithFullG1AShape(UserRole role)
    {
        var user = await SeedUser("active@example.com", AccountStatus.Active, role);
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
        result.Value.Role.Should().Be(role);
        result.Value.Status.Should().Be(AccountStatus.Active);
        result.Value.AccessTokenExpiresAtUtc.Should().Be(_accessTokenExpiresAtUtc);
        result.Value.IsNewAccount.Should().BeFalse();
    }

    [Theory]
    [InlineData(AccountStatus.Active, "auth.admin_google_sign_in_disabled")]
    [InlineData(AccountStatus.Rejected, AuthErrorCodes.AccountStateUnresolved)]
    [InlineData(AccountStatus.PendingEmailVerification, AuthErrorCodes.AccountPendingVerification)]
    [InlineData(AccountStatus.PendingApproval, AuthErrorCodes.AccountStateUnresolved)]
    [InlineData(AccountStatus.Locked, AuthErrorCodes.AccountLocked)]
    [InlineData(AccountStatus.Inactive, AuthErrorCodes.AccountInactive)]
    public async Task Handle_WhenExistingAdministrator_RejectsGoogleWithoutMutationOrSession(
        AccountStatus status, string expectedCode)
    {
        // BR-18 revision 2.1: status failures retain precedence over the admin method gate.
        var user = await SeedUser("admin@example.com", status, UserRole.Administrator);
        var updatedAt = user.UpdatedAtUtc;
        FirebaseReturns(new FirebaseTokenValidationResult(
            "fb-admin", " ADMIN@example.com ", true, "Google Name", "https://photo/admin.png", "google.com"));

        var result = await _handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedCode);
        _jwtTokenService.Verify(j => j.GenerateAccessToken(It.IsAny<User>()), Times.Never);
        _jwtTokenService.Verify(j => j.GenerateRefreshToken(), Times.Never);
        _dbContext.RefreshTokens.Should().BeEmpty();
        user.AvatarUrl.Should().BeNull();
        user.LastLoginAtUtc.Should().BeNull();
        user.UpdatedAtUtc.Should().Be(updatedAt);

        var persistedUser = await _dbContext.Users.FindAsync(user.Id);
        persistedUser!.Role.Should().Be(UserRole.Administrator);
        persistedUser.Status.Should().Be(status);
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

        // P2a: the user signs in at provisioning time — LastLoginAtUtc must be stamped.
        provisioned.LastLoginAtUtc.Should().Be(_dateTimeProvider.Object.UtcNow);
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

    [Theory]
    [InlineData(AccountStatus.PendingApproval, OperatorApprovalStatus.PendingApproval)]
    [InlineData(AccountStatus.Rejected, OperatorApprovalStatus.Rejected)]
    public async Task Handle_WhenVerifiedMatchingLegacyOperator_ReturnsEffectiveActive(AccountStatus status, OperatorApprovalStatus approval)
    {
        var user = await SeedUser("legacy@example.com", status, UserRole.TourOperator);
        user.CreatedAtUtc = _dateTimeProvider.Object.UtcNow.AddDays(-1);
        user.EmailVerifiedAtUtc = _dateTimeProvider.Object.UtcNow;
        _dbContext.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = approval });
        await _dbContext.SaveChangesAsync();
        FirebaseReturnsVerifiedEmail(user.Email!);

        var result = await _handler.Handle(new GoogleAuthCommand("valid-token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(AccountStatus.Active);
        result.Value.ApplicationStatus.Should().Be(approval.ToString());
        user.Status.Should().Be(status);
        _dbContext.OperatorProfiles.Single().ApprovalStatus.Should().Be(approval);
        _dbContext.RefreshTokens.Should().ContainSingle(t => t.UserId == user.Id);
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

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    [InlineData(UserRole.Administrator)]
    public async Task Handle_WhenFirstSaveHitsUniqueViolation_RechecksAccountGate(UserRole role)
    {
        // BR-02 race (spec §4.2-B7): simulate the concurrent winner committing the account
        // between the handler's lookup and its insert — the first SaveChanges fails with a
        // unique violation; the race path must clear the tracker, re-load the account, fall
        // through the status gates and complete the sign-in (never escaping the duplicate
        // insert as a 500, never creating a second user).
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var dbContext = new ThrowingOnceTestDbContext(options);

        var winner = new User
        {
            Email = "race@example.com",
            FullName = "Race Winner",
            Role = role,
            Status = AccountStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.ConcurrentWinner = winner;

        FirebaseReturns(new FirebaseTokenValidationResult(
            "fb-race", "race@example.com", true, null, "https://photo/race.png", "google.com"));

        var handler = new GoogleAuthCommandHandler(
            dbContext,
            _firebaseAuthService.Object,
            _jwtTokenService.Object,
            _dateTimeProvider.Object,
            NullLogger<GoogleAuthCommandHandler>.Instance);
        var result = await handler.Handle(
            new GoogleAuthCommand("valid-google-id-token"),
            CancellationToken.None);

        if (role == UserRole.Administrator)
        {
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be("auth.admin_google_sign_in_disabled");
            dbContext.SaveAttempts.Should().Be(1);
            dbContext.RefreshTokens.Should().BeEmpty();
            _jwtTokenService.Verify(j => j.GenerateAccessToken(It.IsAny<User>()), Times.Never);
            var rejected = await dbContext.Users.SingleAsync(u => u.Email == "race@example.com");
            rejected.Role.Should().Be(UserRole.Administrator);
            rejected.Status.Should().Be(AccountStatus.Active);
            rejected.AvatarUrl.Should().BeNull();
            rejected.LastLoginAtUtc.Should().BeNull();
            rejected.UpdatedAtUtc.Should().Be(winner.UpdatedAtUtc);
            return;
        }

        result.IsSuccess.Should().BeTrue();
        result.Value.IsNewAccount.Should().BeFalse();
        result.Value.UserId.Should().Be(winner.Id);
        result.Value.Role.Should().Be(role);

        var users = await dbContext.Users.AsNoTracking()
            .Where(u => u.Email == "race@example.com").ToListAsync();
        users.Should().ContainSingle("the unique index must prevent a second provisioned account");
        dbContext.SaveAttempts.Should().Be(2);
        var tokens = await dbContext.RefreshTokens.AsNoTracking().ToListAsync();
        tokens.Should().ContainSingle("the failed attempt must not leave a session tracked for retry");
        tokens.Single().UserId.Should().Be(winner.Id);
    }

    [Theory]
    [InlineData(AccountStatus.PendingApproval)]
    [InlineData(AccountStatus.Rejected)]
    public async Task Handle_WhenFirebaseVerifiedButStoredEvidenceMissing_DeniesBeforeMutation(AccountStatus status)
    {
        var user = await SeedUser("no-evidence@example.com", status, UserRole.TourOperator);
        _dbContext.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            ApprovalStatus = status == AccountStatus.Rejected ? OperatorApprovalStatus.Rejected : OperatorApprovalStatus.PendingApproval
        });
        await _dbContext.SaveChangesAsync();
        var updatedAt = user.UpdatedAtUtc;
        FirebaseReturns(new FirebaseTokenValidationResult("uid", user.Email!, true, null, "https://photo/test.png", "google.com"));
        var result = await _handler.Handle(new GoogleAuthCommand("token"), CancellationToken.None);
        result.ErrorCode.Should().Be("auth.account_state_unresolved");
        user.EmailVerifiedAtUtc.Should().BeNull();
        user.Status.Should().Be(status);
        user.AvatarUrl.Should().BeNull();
        user.LastLoginAtUtc.Should().BeNull();
        user.UpdatedAtUtc.Should().Be(updatedAt);
        _dbContext.RefreshTokens.Should().BeEmpty();
        _jwtTokenService.Verify(j => j.GenerateRefreshToken(), Times.Never);
        _jwtTokenService.Verify(j => j.GenerateAccessToken(It.IsAny<User>()), Times.Never);
    }

    [Theory]
    [InlineData(AccountStatus.PendingApproval, true)]
    [InlineData(AccountStatus.Rejected, true)]
    [InlineData(AccountStatus.PendingApproval, false)]
    [InlineData(AccountStatus.Rejected, false)]
    public async Task Handle_WhenLegacyConcurrentWinner_UsesSameResolver(AccountStatus status, bool valid)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ThrowingOnceTestDbContext(options);
        var now = _dateTimeProvider.Object.UtcNow;
        db.ConcurrentWinner = new User
        {
            Email = "legacy-race@example.com",
            FullName = "Winner",
            Role = UserRole.TourOperator,
            Status = status,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now.AddDays(-1),
            EmailVerifiedAtUtc = valid ? now : null
        };
        db.ConcurrentProfile = new OperatorProfile
        {
            User = db.ConcurrentWinner,
            ApprovalStatus = status == AccountStatus.Rejected
            ? OperatorApprovalStatus.Rejected : OperatorApprovalStatus.PendingApproval
        };
        FirebaseReturnsVerifiedEmail("legacy-race@example.com");
        var handler = new GoogleAuthCommandHandler(db, _firebaseAuthService.Object, _jwtTokenService.Object,
            _dateTimeProvider.Object, NullLogger<GoogleAuthCommandHandler>.Instance);
        var result = await handler.Handle(new GoogleAuthCommand("token"), CancellationToken.None);
        var winner = await db.Users.SingleAsync();
        winner.Status.Should().Be(status);
        if (valid)
        {
            result.IsSuccess.Should().BeTrue();
            result.Value.Status.Should().Be(AccountStatus.Active);
            result.Value.ApplicationStatus.Should().Be(status.ToString());
            db.RefreshTokens.Should().ContainSingle(t => t.UserId == winner.Id);
        }
        else
        {
            result.ErrorCode.Should().Be("auth.account_state_unresolved");
            db.RefreshTokens.Should().BeEmpty();
            winner.LastLoginAtUtc.Should().BeNull();
            winner.UpdatedAtUtc.Should().Be(now.AddDays(-1));
            _jwtTokenService.Verify(j => j.GenerateAccessToken(It.IsAny<User>()), Times.Never);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OperatorApprovalStatus.Approved)]
    [InlineData(OperatorApprovalStatus.PendingApproval)]
    [InlineData(OperatorApprovalStatus.Rejected)]
    [InlineData((OperatorApprovalStatus)99)]
    public async Task Handle_WhenProfileChanges_ReadsCurrentStateAndWebSerialization(OperatorApprovalStatus? approval)
    {
        var user = await SeedUser("current@example.com", AccountStatus.Active, UserRole.TourOperator);
        FirebaseReturnsVerifiedEmail(user.Email!);
        if (approval.HasValue) _dbContext.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = approval.Value });
        await _dbContext.SaveChangesAsync();
        var result = await _handler.Handle(new GoogleAuthCommand("token"), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        var expected = approval == (OperatorApprovalStatus)99 ? null : approval?.ToString();
        result.Value.ApplicationStatus.Should().Be(expected);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        options.Converters.Add(new JsonStringEnumConverter());
        using var web = JsonDocument.Parse(JsonSerializer.Serialize(WebGoogleAuthResponseDto.From(result.Value), options));
        web.RootElement.GetProperty("applicationStatus").GetString().Should().Be(expected);
        web.RootElement.GetProperty("isNewAccount").GetBoolean().Should().BeFalse();
        web.RootElement.TryGetProperty("refreshToken", out _).Should().BeFalse();
        using var mobile = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, options));
        mobile.RootElement.GetProperty("refreshToken").GetString().Should().Be("sample-refresh-token");
        mobile.RootElement.TryGetProperty("applicationStatus", out _).Should().BeFalse();
        if (approval.HasValue)
        {
            _dbContext.OperatorProfiles.Single().ApprovalStatus = OperatorApprovalStatus.Approved;
            await _dbContext.SaveChangesAsync();
            var next = await _handler.Handle(new GoogleAuthCommand("token"), CancellationToken.None);
            next.Value.ApplicationStatus.Should().Be("Approved");
        }
    }

    private sealed class ThrowingOnceTestDbContext : TestDbContext
    {
        private readonly DbContextOptions<TestDbContext> _options;

        public ThrowingOnceTestDbContext(DbContextOptions<TestDbContext> options)
            : base(options)
        {
            _options = options;
        }

        public User ConcurrentWinner { get; set; } = null!;
        public OperatorProfile? ConcurrentProfile { get; set; }
        public int SaveAttempts { get; private set; }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveAttempts++;

            if (SaveAttempts == 1)
            {
                // Commit the competing account only after the initial lookup missed it.
                await using var competingContext = new TestDbContext(_options);
                competingContext.Users.Add(ConcurrentWinner);
                if (ConcurrentProfile is not null) competingContext.OperatorProfiles.Add(ConcurrentProfile);
                await competingContext.SaveChangesAsync(cancellationToken);
                throw new DbUpdateException(
                    "simulated unique violation (BR-02 race)",
                    new InvalidOperationException("simulated duplicate insert"));
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}