using FluentAssertions;

using TripMate.Application.Common.Interfaces;

using TripMate.Application.Common.Models;

using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public class InMemoryPasswordResetStateStoreTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private const string ProtectedOtpA = "protected-otp-A";
    private const string ProtectedOtpB = "protected-otp-B";
    private const long UserId = 42;

    private readonly MutableDateTimeProvider _clock;
    private readonly InMemoryPasswordResetStateStore _store;

    public InMemoryPasswordResetStateStoreTests()
    {
        _clock = new MutableDateTimeProvider(BaseTime);
        _store = new InMemoryPasswordResetStateStore(_clock);
    }

    [Fact(DisplayName = "PLAN-STATE-01: Issue state and retrieve the current state")]
    public void Issue_ThenGetCurrent_ReturnsCurrentState()
    {
        var result = _store.Issue(UserId, ProtectedOtpA);

        result.Outcome.Should().Be(PasswordResetIssueOutcome.Issued);
        var current = _store.GetCurrent(UserId);
        current.Should().NotBeNull();
        current!.UserId.Should().Be(UserId);
        current.ProtectedOtp.Should().Be(ProtectedOtpA);
        current.DeliveryState.Should().Be(PasswordResetDeliveryState.Pending);
        current.FailedAttemptCount.Should().Be(0);
        current.CreatedAtUtc.Should().Be(BaseTime);
        current.Generation.Should().BePositive();
    }

    [Fact(DisplayName = "PLAN-STATE-02: State created at T becomes expired at T + 3 minutes")]
    public void StateAt_ExpiryTime_IsNoLongerRetrievable()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;

        issued.ExpiresAtUtc.Should().Be(issued.CreatedAtUtc + PasswordResetPolicy.OtpTimeToLive);

        _clock.UtcNow = issued.ExpiresAtUtc;

        _store.GetCurrent(UserId).Should().BeNull();
    }

    [Fact(DisplayName = "PLAN-STATE-03: State is still valid immediately before the 3-minute expiry boundary")]
    public void StateImmediatelyBefore_ExpiryTime_IsStillUsable()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;

        _clock.UtcNow = issued.ExpiresAtUtc - TimeSpan.FromTicks(1);

        _store.GetCurrent(UserId).Should().NotBeNull();
    }

    [Fact(DisplayName = "PLAN-STATE-04: Cooldown is active before 60 seconds")]
    public void Issue_BeforeCooldownEnd_IsSuppressed()
    {
        _store.Issue(UserId, ProtectedOtpA);

        _clock.UtcNow = BaseTime + PasswordResetPolicy.ResendCooldown - TimeSpan.FromTicks(1);

        var result = _store.Issue(UserId, ProtectedOtpB);

        result.Outcome.Should().Be(PasswordResetIssueOutcome.CooldownSuppressed);
        result.CooldownEndsAtUtc.Should().Be(BaseTime + PasswordResetPolicy.ResendCooldown);
    }

    [Fact(DisplayName = "PLAN-STATE-05: Cooldown ends at/after 60 seconds")]
    public void Issue_ExactlyAtCooldownEnd_IsIssued()
    {
        _store.Issue(UserId, ProtectedOtpA);

        _clock.UtcNow = BaseTime + PasswordResetPolicy.ResendCooldown;

        var result = _store.Issue(UserId, ProtectedOtpB);

        result.Outcome.Should().Be(PasswordResetIssueOutcome.Issued);
    }

    [Fact(DisplayName = "PLAN-STATE-06: A suppressed cooldown operation does not replace the existing state")]
    public void SuppressedIssue_KeepsExistingCurrentState()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;

        _clock.UtcNow = BaseTime + TimeSpan.FromSeconds(30);
        var suppressed = _store.Issue(UserId, ProtectedOtpB);

        suppressed.Outcome.Should().Be(PasswordResetIssueOutcome.CooldownSuppressed);
        suppressed.State.Should().NotBeNull();
        suppressed.State!.Generation.Should().Be(issued.Generation);
        suppressed.State.ProtectedOtp.Should().Be(ProtectedOtpA);
        var current = _store.GetCurrent(UserId);
        current!.Generation.Should().Be(issued.Generation);
        current.ProtectedOtp.Should().Be(ProtectedOtpA);
    }

    [Fact(DisplayName = "PLAN-STATE-07: New accepted generation supersedes the old generation")]
    public void AcceptedIssue_AfterCooldown_SupersedesPreviousGeneration()
    {
        var first = _store.Issue(UserId, ProtectedOtpA).State!;

        _clock.UtcNow = BaseTime + PasswordResetPolicy.ResendCooldown;
        var second = _store.Issue(UserId, ProtectedOtpB).State!;

        second.Generation.Should().BeGreaterThan(first.Generation);
        second.ProtectedOtp.Should().Be(ProtectedOtpB);
        second.CreatedAtUtc.Should().Be(BaseTime + PasswordResetPolicy.ResendCooldown);
        second.ExpiresAtUtc.Should().Be(BaseTime + PasswordResetPolicy.ResendCooldown + PasswordResetPolicy.OtpTimeToLive);
        second.FailedAttemptCount.Should().Be(0);
        var current = _store.GetCurrent(UserId);
        current!.Generation.Should().Be(second.Generation);
        current.ProtectedOtp.Should().Be(ProtectedOtpB);
    }

    [Fact(DisplayName = "PLAN-STATE-08: Stale transition for old generation cannot mutate current generation")]
    public void StaleDeliveryTransition_DoesNotMutateCurrentGeneration()
    {
        var first = _store.Issue(UserId, ProtectedOtpA).State!;

        _clock.UtcNow = BaseTime + PasswordResetPolicy.ResendCooldown;
        var second = _store.Issue(UserId, ProtectedOtpB).State!;

        var staleTransition = _store.TryTransitionDelivery(UserId, first.Generation, PasswordResetDeliveryState.Sent);
        var staleAttempt = _store.RecordFailedAttempt(UserId, first.Generation);

        staleTransition.Should().BeFalse();
        staleAttempt.Accepted.Should().BeFalse();
        var current = _store.GetCurrent(UserId);
        current!.Generation.Should().Be(second.Generation);
        current.DeliveryState.Should().Be(PasswordResetDeliveryState.Pending);
        current.FailedAttemptCount.Should().Be(0);
    }

    [Fact(DisplayName = "PLAN-STATE-09: Matching current generation can transition delivery state")]
    public void MatchingGeneration_CanTransitionDeliveryState()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;

        var transitioned = _store.TryTransitionDelivery(UserId, issued.Generation, PasswordResetDeliveryState.Sent);

        transitioned.Should().BeTrue();
        _store.GetCurrent(UserId)!.DeliveryState.Should().Be(PasswordResetDeliveryState.Sent);
    }

    [Fact(DisplayName = "PLAN-STATE-10: Concurrent issue/replace produces exactly one current generation")]
    public async Task ConcurrentIssues_ProduceExactlyOneCurrentGeneration()
    {
        using var startGate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                startGate.Wait();
                return _store.Issue(UserId, ProtectedOtpA);
            }))
            .ToArray();

        startGate.Set();
        var results = (await Task.WhenAll(tasks)).ToList();
        results.Count(r => r.Outcome == PasswordResetIssueOutcome.Issued).Should().Be(1);
        results.Count(r => r.Outcome == PasswordResetIssueOutcome.CooldownSuppressed).Should().Be(7);
        var current = _store.GetCurrent(UserId);
        current.Should().NotBeNull();
        current!.Generation.Should()
            .Be(results.Single(r => r.Outcome == PasswordResetIssueOutcome.Issued).State!.Generation);
    }

    [Fact(DisplayName = "PLAN-STATE-11: FailedAttemptCount increment is atomic under concurrency")]
    public async Task ConcurrentFailedAttempts_NoIncrementsAreLost()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;
        using var startGate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                startGate.Wait();
                return _store.RecordFailedAttempt(UserId, issued.Generation);
            }))
            .ToArray();

        startGate.Set();
        var results = (await Task.WhenAll(tasks)).ToList();
        results.Count(r => r.Accepted).Should().Be(PasswordResetPolicy.MaxFailedAttempts);
        results.Count(r => r.Invalidated).Should().Be(1);
        results.Where(r => r.Accepted).Select(r => r.FailedAttemptCount)
            .Should().OnlyHaveUniqueItems()
            .And.OnlyContain(count => count >= 1 && count <= PasswordResetPolicy.MaxFailedAttempts);
        results.Max(r => r.FailedAttemptCount).Should().Be(PasswordResetPolicy.MaxFailedAttempts);
        _store.GetCurrent(UserId).Should().BeNull();
    }

    [Fact(DisplayName = "PLAN-STATE-12: 5th failed attempt invalidates the state")]
    public void FifthFailedAttempt_InvalidatesCurrentState()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;

        for (var expectedCount = 1; expectedCount < PasswordResetPolicy.MaxFailedAttempts; expectedCount++)
        {
            var attempt = _store.RecordFailedAttempt(UserId, issued.Generation);
            attempt.Accepted.Should().BeTrue();
            attempt.Invalidated.Should().BeFalse();
            attempt.FailedAttemptCount.Should().Be(expectedCount);
        }

        var fifth = _store.RecordFailedAttempt(UserId, issued.Generation);
        fifth.Accepted.Should().BeTrue();
        fifth.Invalidated.Should().BeTrue();
        fifth.FailedAttemptCount.Should().Be(PasswordResetPolicy.MaxFailedAttempts);
        _store.GetCurrent(UserId).Should().BeNull();

        var sixth = _store.RecordFailedAttempt(UserId, issued.Generation);
        sixth.Accepted.Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-STATE-13: Consumed/removed state cannot be retrieved as current usable state")]
    public void ConsumedState_IsNoLongerRetrievable()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;

        _store.TryConsume(UserId, issued.Generation).Should().BeTrue();
        _store.GetCurrent(UserId).Should().BeNull();
        _store.TryConsume(UserId, issued.Generation).Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-STATE-14: Confirm-time invalidation removes/invalidates current state")]
    public void Invalidation_RemovesCurrentState()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;

        _store.TryInvalidate(UserId, issued.Generation).Should().BeTrue();
        _store.GetCurrent(UserId).Should().BeNull();
    }

    [Fact(DisplayName = "PLAN-STATE-15: New store instance has no knowledge of prior store state")]
    public void NewStoreInstance_HasNoKnowledgeOfPriorState()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;

        var restartedStore = new InMemoryPasswordResetStateStore(_clock);

        restartedStore.GetCurrent(UserId).Should().BeNull();
        var freshIssue = restartedStore.Issue(UserId, ProtectedOtpB);
        freshIssue.Outcome.Should().Be(PasswordResetIssueOutcome.Issued);
        freshIssue.State!.Generation.Should().Be(issued.Generation);
    }

    [Fact(DisplayName = "PLAN-STATE-16: TryConsume for an expired generation returns false and removes the expired state")]
    public void TryConsume_WhenGenerationExpired_ReturnsFalseAndRemovesExpiredState()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;

        _clock.UtcNow = issued.ExpiresAtUtc;

        _store.TryConsume(UserId, issued.Generation).Should().BeFalse();
        _store.GetCurrent(UserId).Should().BeNull();
    }

    [Fact(DisplayName = "PLAN-STATE-17: TryInvalidate for an expired generation returns false and removes the expired state")]
    public void TryInvalidate_WhenGenerationExpired_ReturnsFalseAndRemovesExpiredState()
    {
        var issued = _store.Issue(UserId, ProtectedOtpA).State!;

        _clock.UtcNow = issued.ExpiresAtUtc;

        _store.TryInvalidate(UserId, issued.Generation).Should().BeFalse();
        _store.GetCurrent(UserId).Should().BeNull();
    }

    [Fact(DisplayName = "PLAN-STATE-18: Issue with explicit createdAtUtc stamps exactly that creation time")]
    public void Issue_WithExplicitCreatedAtUtc_UsesProvidedTimestamp()
    {
        var explicitTime = BaseTime + TimeSpan.FromSeconds(123);

        var result = _store.Issue(UserId, ProtectedOtpA, explicitTime);

        result.Outcome.Should().Be(PasswordResetIssueOutcome.Issued);
        result.State!.CreatedAtUtc.Should().Be(explicitTime);
        result.State.ExpiresAtUtc.Should().Be(explicitTime + PasswordResetPolicy.OtpTimeToLive);
    }

    private sealed class MutableDateTimeProvider(DateTimeOffset utcNow)
        : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}