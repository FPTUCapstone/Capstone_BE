using FluentAssertions;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.PasswordReset;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.PasswordReset;

public class ConfirmPasswordResetCommandHandlerTests
{
    private const string Email = "user@example.com";
    private const string CorrectCode = "042731";
    private const string WrongCode = "111111";
    private const string NewPassword = "NewPassword1!";
    private const string OldPasswordHash = "hashed:OldPassword1!";
    private const string ProtectedOtp = "protected-otp-x";
    private const long UserId = 42;
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 1, 0, TimeSpan.Zero);

    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly Mock<IPasswordResetStateStore> _store = new();
    private readonly Mock<IOtpProtectionService> _protectionService = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly Mock<IDateTimeProvider> _clock = new();
    private readonly PasswordResetEligibilityResolver _resolver;

    private readonly ConfirmPasswordResetCommandHandler _handler;

    public ConfirmPasswordResetCommandHandlerTests()
    {
        _clock.SetupGet(c => c.UtcNow).Returns(Now);
        _resolver = new PasswordResetEligibilityResolver(_dbContext);
        _handler = new ConfirmPasswordResetCommandHandler(
            _dbContext,
            _store.Object,
            _protectionService.Object,
            _passwordHasher,
            _resolver,
            _clock.Object,
            NullLogger<ConfirmPasswordResetCommandHandler>.Instance);

        _protectionService
            .Setup(p => p.Verify(CorrectCode, UserId, CreatedAtUtc, ProtectedOtp))
            .Returns(true);
    }

    private User CreateUser(
        AccountStatus status = AccountStatus.Active,
        UserRole role = UserRole.Traveler,
        string? passwordHash = OldPasswordHash)
    {
        var user = new User
        {
            Id = UserId,
            Email = Email,
            FullName = "Test User",
            Role = role,
            Status = status,
            PasswordHash = passwordHash,
        };
        _dbContext.Users.Add(user);
        _dbContext.SaveChanges();
        return user;
    }

    private void SetupCurrentState(long generation = 1, int failedAttemptCount = 0) =>
        _store
            .Setup(s => s.GetCurrent(UserId))
            .Returns(new PasswordResetState(
                UserId, ProtectedOtp, CreatedAtUtc, CreatedAtUtc + PasswordResetPolicy.OtpTimeToLive,
                failedAttemptCount, PasswordResetDeliveryState.Sent, generation));

    [Fact(DisplayName = "PLAN-CONF-01: correct OTP resets the password (hash changed, old invalid, new valid)")]
    public async Task Handle_WithCorrectOtp_ResetsPassword()
    {
        CreateUser();
        SetupCurrentState();
        _store.Setup(s => s.TryConsume(UserId, 1)).Returns(true);

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        var user = await _dbContext.Users.SingleAsync(u => u.Id == UserId);
        result.IsSuccess.Should().BeTrue();
        result.Value.Message.Should().Be(ConfirmPasswordResetResponse.SuccessMessage);
        user.PasswordHash.Should().Be(_passwordHasher.Hash(NewPassword));
        user.PasswordHash.Should().NotBe(OldPasswordHash);
        _passwordHasher.Verify(OldPassword1(), user.PasswordHash).Should().BeFalse();
        _passwordHasher.Verify(NewPassword, user.PasswordHash).Should().BeTrue();
        _store.Verify(s => s.TryConsume(UserId, 1), Times.Once);
        _store.Verify(s => s.RecordFailedAttempt(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    private static string OldPassword1() => "OldPassword1!";

    [Fact(DisplayName = "PLAN-CONF-02: all active refresh tokens are revoked on success")]
    public async Task Handle_OnSuccess_RevokesAllActiveRefreshTokens()
    {
        var user = CreateUser();
        _dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            User = user,
            TokenHash = "hash-1",
            CreatedAtUtc = Now,
            ExpiresAtUtc = Now.AddDays(1),
        });
        _dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            User = user,
            TokenHash = "hash-2",
            CreatedAtUtc = Now,
            ExpiresAtUtc = Now.AddDays(1),
        });
        _dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            User = user,
            TokenHash = "hash-revoked",
            CreatedAtUtc = Now,
            ExpiresAtUtc = Now.AddDays(1),
            RevokedAtUtc = Now.AddDays(-1),
        });
        _dbContext.SaveChanges();
        SetupCurrentState();
        _store.Setup(s => s.TryConsume(UserId, 1)).Returns(true);

        await _handler.Handle(new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        var tokens = _dbContext.RefreshTokens.ToList();
        tokens.Where(t => t.TokenHash != "hash-revoked")
            .Should().OnlyContain(t => t.RevokedAtUtc == Now);
        tokens.Single(t => t.TokenHash == "hash-revoked").RevokedAtUtc.Should().Be(Now.AddDays(-1));
    }

    [Fact(DisplayName = "PLAN-CONF-03: OTP is single-use — replay is rejected with MSG14")]
    public async Task Handle_ReplayedOtp_IsRejected()
    {
        CreateUser();
        // Stateful mock mirroring the real store: the state is gone once consumed.
        PasswordResetState? current = new(
            UserId, ProtectedOtp, CreatedAtUtc, CreatedAtUtc + PasswordResetPolicy.OtpTimeToLive,
            0, PasswordResetDeliveryState.Sent, 1);
        _store.Setup(s => s.GetCurrent(UserId)).Returns(() => current);
        _store.Setup(s => s.TryConsume(UserId, 1)).Returns(true).Callback(() => current = null);

        await _handler.Handle(new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);
        var replay = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be(AuthErrorCodes.Msg14);
        _store.Verify(s => s.TryConsume(UserId, 1), Times.Once);
    }

    [Fact(DisplayName = "PLAN-CONF-04: expired/missing state is rejected without attempt increment")]
    public async Task Handle_WhenStateMissing_IsRejectedWithoutIncrement()
    {
        CreateUser();
        _store.Setup(s => s.GetCurrent(UserId)).Returns((PasswordResetState?)null);

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg14);
        _store.Verify(s => s.RecordFailedAttempt(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    [Theory(DisplayName = "PLAN-CONF-05: Pending/Failed/Unknown delivery state is rejected without increment")]
    [InlineData(PasswordResetDeliveryState.Pending)]
    [InlineData(PasswordResetDeliveryState.Failed)]
    [InlineData(PasswordResetDeliveryState.Unknown)]
    public async Task Handle_WhenDeliveryStateNotSent_IsRejectedWithoutIncrement(
        PasswordResetDeliveryState deliveryState)
    {
        CreateUser();
        _store.Setup(s => s.GetCurrent(UserId)).Returns(new PasswordResetState(
            UserId, ProtectedOtp, CreatedAtUtc, CreatedAtUtc + PasswordResetPolicy.OtpTimeToLive,
            0, deliveryState, 1));

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg14);
        _store.Verify(s => s.RecordFailedAttempt(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
        _store.Verify(s => s.TryConsume(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    [Fact(DisplayName = "PLAN-CONF-06: real OTP mismatch increments FailedAttemptCount exactly once")]
    public async Task Handle_WithWrongOtp_IncrementsExactlyOnce()
    {
        CreateUser();
        SetupCurrentState();

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, WrongCode, NewPassword), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg14);
        _store.Verify(s => s.RecordFailedAttempt(UserId, 1), Times.Once);
        _store.Verify(s => s.TryConsume(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    [Fact(DisplayName = "PLAN-CONF-07: the fifth mismatch invalidates the state via the store")]
    public async Task Handle_OnFifthMismatch_StoreInvalidatesState()
    {
        CreateUser();
        SetupCurrentState(failedAttemptCount: PasswordResetPolicy.MaxFailedAttempts - 1);

        await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, WrongCode, NewPassword), CancellationToken.None);

        _store.Verify(s => s.RecordFailedAttempt(UserId, 1), Times.Once);
    }

    [Fact(DisplayName = "PLAN-CONF-08: exhausted/invalidated state is not incremented again")]
    public async Task Handle_WhenStateAlreadyGone_DoesNotIncrement()
    {
        CreateUser();
        _store.Setup(s => s.GetCurrent(UserId)).Returns((PasswordResetState?)null);

        await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, WrongCode, NewPassword), CancellationToken.None);

        _store.Verify(s => s.RecordFailedAttempt(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    [Fact(DisplayName = "PLAN-CONF-09: cross-account — OTP of A with email of B counts as B's wrong attempt")]
    public async Task Handle_CrossAccountOtp_IncrementsOnlyTargetAccount()
    {
        CreateUser(); // account B has its own Sent state, but the protected OTP belongs to A
        SetupCurrentState();
        // B's state stores A's protected OTP; verification against B's binding fails.
        _protectionService
            .Setup(p => p.Verify(CorrectCode, UserId, CreatedAtUtc, ProtectedOtp))
            .Returns(false);

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg14);
        _store.Verify(s => s.RecordFailedAttempt(UserId, 1), Times.Once);
        _store.Verify(s => s.RecordFailedAttempt(It.IsAny<long>(), It.IsAny<long>()), Times.Once);
    }

    [Fact(DisplayName = "PLAN-CONF-10: confirm-time Locked account invalidates the state and returns MSG14")]
    public async Task Handle_WhenAccountLockedAtConfirm_InvalidatesStateAndFails()
    {
        CreateUser(status: AccountStatus.Locked);
        SetupCurrentState();

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg14);
        _store.Verify(s => s.TryInvalidate(UserId, 1), Times.Once);
        _store.Verify(s => s.RecordFailedAttempt(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
        _store.Verify(s => s.TryConsume(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
        (await _dbContext.Users.SingleAsync(u => u.Id == UserId)).PasswordHash.Should().Be(OldPasswordHash);
    }

    [Fact(DisplayName = "PLAN-CONF-11: confirm-time Inactive account invalidates the state and returns MSG14")]
    public async Task Handle_WhenAccountInactiveAtConfirm_InvalidatesStateAndFails()
    {
        CreateUser(status: AccountStatus.Inactive);
        SetupCurrentState();

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        result.ErrorCode.Should().Be(AuthErrorCodes.Msg14);
        _store.Verify(s => s.TryInvalidate(UserId, 1), Times.Once);
        _store.Verify(s => s.RecordFailedAttempt(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    [Fact(DisplayName = "PLAN-CONF-12: Google-only account at confirm invalidates the state and returns MSG14")]
    public async Task Handle_WhenGoogleOnlyAtConfirm_InvalidatesStateAndFails()
    {
        CreateUser(passwordHash: null);
        SetupCurrentState();

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        result.ErrorCode.Should().Be(AuthErrorCodes.Msg14);
        _store.Verify(s => s.TryInvalidate(UserId, 1), Times.Once);
        _store.Verify(s => s.TryConsume(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    [Fact(DisplayName = "PLAN-CONF-13: PendingEmailVerification local account resets without status mutation")]
    public async Task Handle_PendingEmailVerificationAccount_ResetsWithoutStatusMutation()
    {
        CreateUser(status: AccountStatus.PendingEmailVerification);
        SetupCurrentState();
        _store.Setup(s => s.TryConsume(UserId, 1)).Returns(true);

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await _dbContext.Users.SingleAsync(u => u.Id == UserId)).Status
            .Should().Be(AccountStatus.PendingEmailVerification);
    }

    [Fact(DisplayName = "PLAN-CONF-14: Administrator local-password account resets through the shared flow")]
    public async Task Handle_AdministratorAccount_ResetsSuccessfully()
    {
        CreateUser(role: UserRole.Administrator);
        SetupCurrentState();
        _store.Setup(s => s.TryConsume(UserId, 1)).Returns(true);

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await _dbContext.Users.SingleAsync(u => u.Id == UserId)).Role.Should().Be(UserRole.Administrator);
    }

    [Fact(DisplayName = "PLAN-CONF-15: forced DB failure returns MSG127 and never consumes the reset state")]
    public async Task Handle_WhenDbFails_ReturnsSystemFailureWithoutConsumingState()
    {
        CreateUser();
        SetupCurrentState();
        var failingDbContext = new FailingSaveChangesDbContext();
        var user = new User
        {
            Id = UserId,
            Email = Email,
            FullName = "Test User",
            Status = AccountStatus.Active,
            PasswordHash = OldPasswordHash,
        };
        failingDbContext.Users.Add(user);
        failingDbContext.SaveChanges();
        var resolver = new PasswordResetEligibilityResolver(failingDbContext);
        var handler = new ConfirmPasswordResetCommandHandler(
            failingDbContext, _store.Object, _protectionService.Object, _passwordHasher, resolver, _clock.Object,
            NullLogger<ConfirmPasswordResetCommandHandler>.Instance);

        var result = await handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        _store.Verify(s => s.TryConsume(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    [Fact(DisplayName = "PLAN-CONF-16: TryConsume false after a committed reset does not turn success into failure")]
    public async Task Handle_TryConsumeFalseAfterCommit_StillSucceeds()
    {
        CreateUser();
        SetupCurrentState();
        _store.Setup(s => s.TryConsume(UserId, 1)).Returns(false);

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        // The DB transaction is authoritative once committed: the reset state may have been
        // lazily removed after crossing ExpiresAtUtc.
        result.IsSuccess.Should().BeTrue();
        result.Value.Message.Should().Be(ConfirmPasswordResetResponse.SuccessMessage);
    }

    [Fact(DisplayName = "PLAN-CONF-17: no status/role mutation during confirm")]
    public async Task Handle_DoesNotMutateStatusOrRole()
    {
        var user = CreateUser(status: AccountStatus.PendingEmailVerification, role: UserRole.TourOperator);
        _dbContext.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            User = user,
            CompanyName = "Co",
            TaxCode = "T",
            BusinessLicenseNo = "B",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        _dbContext.SaveChanges();
        SetupCurrentState();
        _store.Setup(s => s.TryConsume(UserId, 1)).Returns(true);

        await _handler.Handle(
            new ConfirmPasswordResetCommand(Email, CorrectCode, NewPassword), CancellationToken.None);

        var updated = await _dbContext.Users.SingleAsync(u => u.Id == UserId);
        updated.Status.Should().Be(AccountStatus.PendingEmailVerification);
        updated.Role.Should().Be(UserRole.TourOperator);
        _dbContext.OperatorProfiles.Single(p => p.UserId == UserId).ApprovalStatus
            .Should().Be(OperatorApprovalStatus.Approved);
    }

    private sealed class FailingSaveChangesDbContext : TestDbContext
    {
        public FailingSaveChangesDbContext()
            : base(new DbContextOptionsBuilder<TestDbContext>()
                .UseInMemoryDatabase($"tripmate-confirm-fail-{Guid.NewGuid():N}")
                .Options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated database failure.");
    }
}