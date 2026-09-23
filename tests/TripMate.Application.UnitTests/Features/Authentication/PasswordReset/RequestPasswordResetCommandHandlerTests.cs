using FluentAssertions;

using MediatR;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.PasswordReset;
using TripMate.Application.UnitTests.TestUtilities;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.PasswordReset;

public class RequestPasswordResetCommandHandlerTests
{
    private const string Email = "user@example.com";
    private const string RawOtp = "042731";
    private const string ProtectedOtp = "protected-otp-x";
    private const long UserId = 42;
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly Mock<IPasswordResetEligibilityResolver> _resolver = new();
    private readonly Mock<IPasswordResetStateStore> _store = new();
    private readonly Mock<IOtpCodeGenerator> _otpGenerator = new();
    private readonly Mock<IOtpProtectionService> _protectionService = new();
    private readonly Mock<IEmailSender> _emailSender = new();
    private readonly Mock<IDateTimeProvider> _clock = new();
    private readonly FakeTimingNormalizer _timingNormalizer = new();
    private readonly Queue<DateTimeOffset> _clockValues = new();

    private readonly RequestPasswordResetCommandHandler _handler;

    public RequestPasswordResetCommandHandlerTests()
    {
        _clock.SetupGet(c => c.UtcNow).Returns(() =>
            _clockValues.Count > 0 ? _clockValues.Dequeue() : Now);
        _otpGenerator.Setup(g => g.Generate()).Returns(RawOtp);
        _protectionService
            .Setup(p => p.Protect(RawOtp, UserId, It.IsAny<DateTimeOffset>()))
            .Returns(ProtectedOtp);
        _emailSender
            .Setup(s => s.SendPasswordResetOtpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmailDeliveryResult.Delivered);

        _handler = new RequestPasswordResetCommandHandler(
            _resolver.Object,
            _store.Object,
            new FakePasswordResetAccountLock(),
            _otpGenerator.Object,
            _protectionService.Object,
            _emailSender.Object,
            _timingNormalizer,
            _clock.Object);
    }

    private void SetupEligible() =>
        _resolver
            .Setup(r => r.ResolveEligibleLocalPasswordAccountAsync(Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(UserId));

    private void SetupResolverFailure() =>
        _resolver
            .Setup(r => r.ResolveEligibleLocalPasswordAccountAsync(Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<long>("MSG14", "Invalid or expired password reset request."));

    private void SetupStoreIssuesGeneration(long generation) =>
        _store
            .Setup(s => s.Issue(UserId, ProtectedOtp, Now))
            .Returns(new PasswordResetIssueResult(
                PasswordResetIssueOutcome.Issued,
                new PasswordResetState(
                    UserId, ProtectedOtp, Now, Now + PasswordResetPolicy.OtpTimeToLive, 0,
                    PasswordResetDeliveryState.Pending, generation),
                CooldownEndsAtUtc: null));

    private void SetupStoreCooldownSuppressed() =>
        _store
            .Setup(s => s.Issue(UserId, ProtectedOtp, Now))
            .Returns(new PasswordResetIssueResult(
                PasswordResetIssueOutcome.CooldownSuppressed,
                new PasswordResetState(
                    UserId, ProtectedOtp, Now, Now + PasswordResetPolicy.OtpTimeToLive, 0,
                    PasswordResetDeliveryState.Sent, 1),
                Now + PasswordResetPolicy.ResendCooldown));

    private static PasswordResetState CurrentState(long generation, PasswordResetDeliveryState deliveryState) =>
        new(UserId, ProtectedOtp, Now, Now + PasswordResetPolicy.OtpTimeToLive, 0, deliveryState, generation);

    [Fact(DisplayName = "PLAN-REQ-01: eligible account issues a reset generation")]
    public async Task Handle_WhenEligible_IssuesGeneration()
    {
        SetupEligible();
        SetupStoreIssuesGeneration(1);

        var result = await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Message.Should().Be(RequestPasswordResetResponse.GenericMessage);
        _store.Verify(s => s.Issue(UserId, ProtectedOtp, Now), Times.Once);
    }

    [Fact(DisplayName = "PLAN-REQ-02: only the protected OTP is stored, never the raw OTP")]
    public async Task Handle_StoresOnlyProtectedOtp()
    {
        SetupEligible();
        SetupStoreIssuesGeneration(1);

        var result = await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        _store.Verify(s => s.Issue(UserId, ProtectedOtp, Now), Times.Once);
        _store.Verify(
            s => s.Issue(It.IsAny<long>(), RawOtp, It.IsAny<DateTimeOffset>()),
            Times.Never,
            "the raw OTP must never reach the state store");
        result.Value.Message.Should().NotContain(RawOtp);
    }

    [Fact(DisplayName = "PLAN-REQ-03: Delivered transitions the matching generation to Sent")]
    public async Task Handle_WhenDelivered_TransitionsGenerationToSent()
    {
        SetupEligible();
        SetupStoreIssuesGeneration(7);

        await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        _store.Verify(s => s.TryTransitionDelivery(UserId, 7, PasswordResetDeliveryState.Sent), Times.Once);
        _store.Verify(s => s.TryInvalidate(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    [Fact(DisplayName = "PLAN-REQ-04: DefiniteFailure invalidates the matching generation")]
    public async Task Handle_WhenDefiniteFailure_InvalidatesGeneration()
    {
        SetupEligible();
        SetupStoreIssuesGeneration(7);
        _emailSender
            .Setup(s => s.SendPasswordResetOtpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmailDeliveryResult.DefiniteFailure);

        await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        _store.Verify(s => s.TryInvalidate(UserId, 7), Times.Once);
        _store.Verify(
            s => s.TryTransitionDelivery(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<PasswordResetDeliveryState>()),
            Times.Never);
    }

    [Fact(DisplayName = "PLAN-REQ-05: Unknown delivery invalidates the matching generation")]
    public async Task Handle_WhenDeliveryUnknown_InvalidatesGeneration()
    {
        SetupEligible();
        SetupStoreIssuesGeneration(7);
        _emailSender
            .Setup(s => s.SendPasswordResetOtpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmailDeliveryResult.Unknown);

        await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        _store.Verify(s => s.TryInvalidate(UserId, 7), Times.Once);
    }

    [Fact(DisplayName = "PLAN-REQ-06: unknown email returns generic success with no state and no email")]
    public async Task Handle_WhenUnknownEmail_ReturnsGenericSuccessWithoutSideEffects()
    {
        SetupResolverFailure();

        var result = await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Message.Should().Be(RequestPasswordResetResponse.GenericMessage);
        _store.Verify(s => s.Issue(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
        _emailSender.Verify(
            s => s.SendPasswordResetOtpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact(DisplayName = "PLAN-REQ-07: Google-only account returns generic success with no state and no email")]
    public async Task Handle_WhenGoogleOnly_ReturnsGenericSuccessWithoutSideEffects()
    {
        // The eligibility resolver already classifies Google-only accounts as ineligible;
        // the handler only observes the generic failure.
        SetupResolverFailure();

        var result = await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        result.Value.Message.Should().Be(RequestPasswordResetResponse.GenericMessage);
        _store.Verify(s => s.Issue(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
        _emailSender.Verify(
            s => s.SendPasswordResetOtpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact(DisplayName = "PLAN-REQ-08: Locked/Inactive account returns generic success with no state and no email")]
    public async Task Handle_WhenLockedOrInactive_ReturnsGenericSuccessWithoutSideEffects()
    {
        SetupResolverFailure();

        var result = await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        result.Value.Message.Should().Be(RequestPasswordResetResponse.GenericMessage);
        _store.Verify(s => s.Issue(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
        _emailSender.Verify(
            s => s.SendPasswordResetOtpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact(DisplayName = "PLAN-REQ-09: cooldown returns generic success with no new OTP, email, or state replacement")]
    public async Task Handle_WhenCooldownSuppressed_SendsNoEmailAndKeepsState()
    {
        SetupEligible();
        SetupStoreCooldownSuppressed();

        var result = await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        result.Value.Message.Should().Be(RequestPasswordResetResponse.GenericMessage);
        _emailSender.Verify(
            s => s.SendPasswordResetOtpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _store.Verify(
            s => s.TryTransitionDelivery(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<PasswordResetDeliveryState>()),
            Times.Never);
        _store.Verify(s => s.TryInvalidate(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
    }

    [Fact(DisplayName = "PLAN-REQ-10: resend after cooldown supersedes the old generation")]
    public async Task Handle_ResendAfterCooldown_SupersedesOldGeneration()
    {
        SetupEligible();
        _store
            .SetupSequence(s => s.Issue(UserId, ProtectedOtp, It.IsAny<DateTimeOffset>()))
            .Returns(new PasswordResetIssueResult(
                PasswordResetIssueOutcome.Issued,
                CurrentState(1, PasswordResetDeliveryState.Pending),
                CooldownEndsAtUtc: null))
            .Returns(new PasswordResetIssueResult(
                PasswordResetIssueOutcome.Issued,
                CurrentState(2, PasswordResetDeliveryState.Pending),
                CooldownEndsAtUtc: null));
        _clockValues.Enqueue(Now);
        _clockValues.Enqueue(Now.AddMinutes(1));

        await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);
        await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        _store.Verify(s => s.TryTransitionDelivery(UserId, 1, PasswordResetDeliveryState.Sent), Times.Once);
        _store.Verify(s => s.TryTransitionDelivery(UserId, 2, PasswordResetDeliveryState.Sent), Times.Once);
        _emailSender.Verify(
            s => s.SendPasswordResetOtpAsync(It.IsAny<string>(), RawOtp, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact(DisplayName = "PLAN-REQ-13: identical generic success contract across account outcomes")]
    public async Task Handle_AcrossOutcomes_ReturnsIdenticalGenericContract()
    {
        var results = new List<Result<RequestPasswordResetResponse>>();

        SetupEligible();
        SetupStoreIssuesGeneration(1);
        results.Add(await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None));

        SetupResolverFailure();
        results.Add(await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None));

        SetupEligible();
        SetupStoreCooldownSuppressed();
        results.Add(await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None));

        _emailSender
            .Setup(s => s.SendPasswordResetOtpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmailDeliveryResult.DefiniteFailure);
        SetupStoreIssuesGeneration(2);
        results.Add(await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None));

        results.Should().OnlyContain(r => r.IsSuccess);
        results.Select(r => r.Value.Message)
            .Should().OnlyContain(message => message == RequestPasswordResetResponse.GenericMessage);
    }

    [Fact(DisplayName = "PLAN-REQ-14: raw OTP is absent from result and store interactions")]
    public async Task Handle_RawOtpNeverAppearsInResultOrStore()
    {
        SetupEligible();
        SetupStoreIssuesGeneration(1);

        var result = await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        result.Value.Message.Should().Be(RequestPasswordResetResponse.GenericMessage);
        result.Value.Message.Should().NotContain(RawOtp);
        _store.Verify(s => s.Issue(UserId, ProtectedOtp, Now), Times.Once);
        _store.Verify(
            s => s.Issue(It.IsAny<long>(), RawOtp, It.IsAny<DateTimeOffset>()),
            Times.Never);
        _store.Verify(
            s => s.GetCurrent(It.IsAny<long>()),
            Times.Never);
    }

    [Fact(DisplayName = "PLAN-REQ-15: timing normalization runs for every account-specific outcome")]
    public async Task Handle_NormalizesTiming_OnEveryOutcome()
    {
        SetupEligible();
        SetupStoreIssuesGeneration(1);
        await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        SetupResolverFailure();
        await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        SetupEligible();
        SetupStoreCooldownSuppressed();
        await _handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        _timingNormalizer.Calls.Should().HaveCount(3);
        _timingNormalizer.Calls.Should().OnlyContain(startedAt => startedAt == Now);
    }

    private sealed class FakeTimingNormalizer : IRequestTimingNormalizer
    {
        public List<DateTimeOffset> Calls { get; } = new();

        public Task EnsureMinimumDurationAsync(DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
        {
            Calls.Add(startedAtUtc);
            return Task.CompletedTask;
        }
    }
}