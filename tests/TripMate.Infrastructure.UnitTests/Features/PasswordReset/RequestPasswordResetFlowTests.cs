using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.PasswordReset;
using TripMate.Infrastructure.Security;
using TripMate.Infrastructure.Services;

using Xunit;

namespace TripMate.Infrastructure.UnitTests.Features.PasswordReset;

public class RequestPasswordResetFlowTests
{
    private const string Email = "user@example.com";
    private const string RawOtp = "042731";
    private const long UserId = 42;
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly MutableDateTimeProvider _clock = new(BaseTime);
    private readonly InMemoryPasswordResetStateStore _store;
    private readonly ScriptableEmailQueue _emailQueue = new();

    public RequestPasswordResetFlowTests()
    {
        _store = new InMemoryPasswordResetStateStore(_clock);
    }

    private RequestPasswordResetCommandHandler CreateHandler() =>
        new(
            new FixedEligibilityResolver(UserId),
            _store,
            new InMemoryPasswordResetAccountLock(),
            new FixedOtpCodeGenerator(RawOtp),
            new HmacOtpProtectionService(Options.Create(new PasswordResetSecurityOptions
            {
                OtpPepper = "test-only-pepper-0123456789abcdef",
            })),
            _emailQueue,
            new NoOpTimingNormalizer(),
            _clock);

    [Fact(DisplayName = "PLAN-REQ-11: stale delivery result cannot affect the newer generation")]
    public async Task Handle_StaleDeliveryResult_DoesNotMutateNewerGeneration()
    {
        var handler = CreateHandler();
        var protectedOtpOfNewGeneration = string.Empty;
        _emailQueue.OnEnqueue = () =>
        {
            // While the first request hands off delivery, the cooldown expires and a resend
            // supersedes the generation that request created.
            _clock.UtcNow = BaseTime + PasswordResetPolicy.ResendCooldown;
            var newOtpProtected = "resend-protected-otp";
            _store.Issue(UserId, newOtpProtected, _clock.UtcNow);
            protectedOtpOfNewGeneration = newOtpProtected;
            return true;
        };

        await handler.Handle(new RequestPasswordResetCommand(Email), CancellationToken.None);

        var current = _store.GetCurrent(UserId);
        current.Should().NotBeNull();
        current!.Generation.Should().Be(2);
        current.DeliveryState.Should().Be(PasswordResetDeliveryState.Pending);
        current.ProtectedOtp.Should().Be(protectedOtpOfNewGeneration);
    }

    [Fact(DisplayName = "PLAN-REQ-12: concurrent requests produce at most one usable current generation")]
    public async Task ConcurrentRequests_ProduceAtMostOneUsableGeneration()
    {
        var handler = CreateHandler();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => handler.Handle(
                new RequestPasswordResetCommand(Email), CancellationToken.None))));

        responses.Should().OnlyContain(r => r.IsSuccess);
        responses.Select(r => r.Value.Message)
            .Should().OnlyContain(message => message == RequestPasswordResetResponse.GenericMessage);
        _emailQueue.EnqueueCount.Should().Be(1);

        var current = _store.GetCurrent(UserId);
        current.Should().NotBeNull();
        current!.DeliveryState.Should().Be(PasswordResetDeliveryState.Pending);

        // The HMAC is bound to exactly the CreatedAtUtc the store recorded, so verification
        // against the stored generation succeeds.
        var protector = new HmacOtpProtectionService(Options.Create(new PasswordResetSecurityOptions
        {
            OtpPepper = "test-only-pepper-0123456789abcdef",
        }));
        protector.Verify(RawOtp, UserId, current.CreatedAtUtc, current.ProtectedOtp).Should().BeTrue();
    }

    private sealed class FixedEligibilityResolver(long userId) : IPasswordResetEligibilityResolver
    {
        public Task<Result<long>> ResolveEligibleLocalPasswordAccountAsync(
            string email,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(userId));
    }

    private sealed class FixedOtpCodeGenerator(string code) : IOtpCodeGenerator
    {
        public string Generate() => code;
    }

    private sealed class ScriptableEmailQueue : IPasswordResetEmailQueue
    {
        public Func<bool> OnEnqueue { get; set; } = () => true;

        public int EnqueueCount { get; private set; }

        public bool TryEnqueue(PasswordResetEmailDelivery delivery)
        {
            EnqueueCount++;
            return OnEnqueue();
        }
    }

    private sealed class NoOpTimingNormalizer : IRequestTimingNormalizer
    {
        public Task EnsureMinimumDurationAsync(DateTimeOffset startedAtUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class MutableDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}