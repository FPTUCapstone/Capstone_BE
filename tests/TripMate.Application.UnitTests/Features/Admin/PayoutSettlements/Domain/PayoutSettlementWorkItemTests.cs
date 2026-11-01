using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Admin.PayoutSettlements.Domain;

public sealed class PayoutSettlementWorkItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollWindow = TimeSpan.FromHours(24);

    [Fact]
    public void Create_StartsPendingAndDueImmediately()
    {
        var item = PayoutSettlementWorkItem.Create(42, Now);

        item.PayoutId.Should().Be(42);
        item.State.Should().Be(PayoutSettlementWorkItem.StatePending);
        item.AttemptCount.Should().Be(0);
        item.NextAttemptAtUtc.Should().Be(Now);
        item.CreatedAtUtc.Should().Be(Now);
        item.LeaseToken.Should().BeNull();
        item.GatewayReference.Should().BeNull();
        item.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Create_WithInvalidPayoutId_Throws()
    {
        var act = () => PayoutSettlementWorkItem.Create(0, Now);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryClaim_WhenPendingAndDue_StartsSubmitAttemptUnderLease()
    {
        var item = PayoutSettlementWorkItem.Create(42, Now);
        var token = Guid.NewGuid();

        var claimed = item.TryClaim(Now, token, Lease);

        claimed.Should().BeTrue();
        item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitting);
        item.AttemptCount.Should().Be(1);
        item.LeaseToken.Should().Be(token);
        item.LeaseExpiresAtUtc.Should().Be(Now + Lease);
        item.NextAttemptAtUtc.Should().BeNull();
    }

    [Fact]
    public void TryClaim_BeforeDueTime_OrWhileLeased_IsRefused()
    {
        var notDue = PayoutSettlementWorkItem.Create(42, Now);
        notDue.TryClaim(Now.AddSeconds(-1), Guid.NewGuid(), Lease).Should().BeFalse();
        notDue.State.Should().Be(PayoutSettlementWorkItem.StatePending);

        var leased = PayoutSettlementWorkItem.Create(43, Now);
        leased.TryClaim(Now, Guid.NewGuid(), Lease).Should().BeTrue();
        leased.TryClaim(Now.AddMinutes(1), Guid.NewGuid(), Lease).Should().BeFalse("only one worker may hold the item");
        leased.AttemptCount.Should().Be(1);
    }

    [Fact]
    public void RecordSubmitted_OnClaimedItem_StoresReferenceDeadlineAndFirstPoll()
    {
        var (item, token) = ClaimedForSubmit();

        var changed = item.RecordSubmitted(token, "payos-123", Now, PollWindow, Now.AddSeconds(30));

        changed.Should().BeTrue();
        item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
        item.GatewayReference.Should().Be("payos-123");
        item.SubmittedAtUtc.Should().Be(Now);
        item.PollDeadlineAtUtc.Should().Be(Now + PollWindow);
        item.NextAttemptAtUtc.Should().Be(Now.AddSeconds(30));
        item.LeaseToken.Should().BeNull();
        item.LastErrorCode.Should().BeNull();
    }

    [Fact]
    public void RecordSubmitted_WithWrongLeaseToken_IsRefused()
    {
        var (item, _) = ClaimedForSubmit();

        item.RecordSubmitted(Guid.NewGuid(), "payos-123", Now, PollWindow, Now).Should().BeFalse();

        item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitting);
        item.GatewayReference.Should().BeNull();
    }

    [Fact]
    public void ScheduleSubmitRetry_ReturnsToPendingWithNextAttemptAndErrorCode()
    {
        var (item, token) = ClaimedForSubmit();

        var changed = item.ScheduleSubmitRetry(token, "gateway_unavailable", Now.AddMinutes(1));

        changed.Should().BeTrue();
        item.State.Should().Be(PayoutSettlementWorkItem.StatePending);
        item.NextAttemptAtUtc.Should().Be(Now.AddMinutes(1));
        item.LastErrorCode.Should().Be("gateway_unavailable");
        item.LeaseToken.Should().BeNull();
        item.AttemptCount.Should().Be(1);
    }

    [Fact]
    public void RetryAfterSchedule_IncrementsAttemptCountOnNextClaim()
    {
        var (item, token) = ClaimedForSubmit();
        item.ScheduleSubmitRetry(token, "gateway_unavailable", Now.AddMinutes(1));

        item.TryClaim(Now.AddMinutes(1), Guid.NewGuid(), Lease).Should().BeTrue();

        item.AttemptCount.Should().Be(2);
    }

    [Fact]
    public void ClaimForPoll_OnSubmittedItem_KeepsStateAndHoldsLease_ThenRecordPollSchedulesNext()
    {
        var (item, token) = SubmittedItem();
        var pollToken = Guid.NewGuid();
        var pollTime = Now.AddSeconds(30);

        item.TryClaim(pollTime, pollToken, Lease).Should().BeTrue();
        item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
        item.AttemptCount.Should().Be(1, "polling does not consume submit attempts");
        item.LeaseToken.Should().Be(pollToken);

        item.RecordPoll(pollToken, pollTime, pollTime.AddMinutes(1)).Should().BeTrue();

        item.PollCount.Should().Be(1);
        item.LastPolledAtUtc.Should().Be(pollTime);
        item.NextAttemptAtUtc.Should().Be(pollTime.AddMinutes(1));
        item.LeaseToken.Should().BeNull();
        token.Should().NotBe(pollToken);
    }

    [Fact]
    public void IsPastPollDeadline_IsFalseBeforeAndTrueAfterDeadline()
    {
        var (item, _) = SubmittedItem();

        item.IsPastPollDeadline(Now + PollWindow).Should().BeFalse();
        item.IsPastPollDeadline(Now + PollWindow + TimeSpan.FromTicks(1)).Should().BeTrue();
        PayoutSettlementWorkItem.Create(1, Now).IsPastPollDeadline(Now.AddYears(1)).Should().BeFalse();
    }

    [Fact]
    public void Complete_FromSubmittedOrSubmitting_IsTerminalAndClearsSchedule()
    {
        var (submitted, _) = SubmittedItem();
        var pollToken = Guid.NewGuid();
        submitted.TryClaim(Now.AddSeconds(30), pollToken, Lease);

        submitted.Complete(pollToken, Now.AddSeconds(31)).Should().BeTrue();

        submitted.State.Should().Be(PayoutSettlementWorkItem.StateCompleted);
        submitted.CompletedAtUtc.Should().Be(Now.AddSeconds(31));
        submitted.NextAttemptAtUtc.Should().BeNull();
        submitted.LeaseToken.Should().BeNull();

        var (submitting, token) = ClaimedForSubmit();
        submitting.Complete(token, Now).Should().BeTrue("a definitive rejection completes the item without a submission");
        submitting.State.Should().Be(PayoutSettlementWorkItem.StateCompleted);
    }

    [Fact]
    public void RequireReconciliation_IsTerminalKeepsGatewayReferenceAndDoesNotComplete()
    {
        var (item, _) = SubmittedItem();
        var pollToken = Guid.NewGuid();
        item.TryClaim(Now + PollWindow + TimeSpan.FromMinutes(1), pollToken, Lease);

        var changed = item.RequireReconciliation(pollToken, "poll_deadline_exceeded");

        changed.Should().BeTrue();
        item.State.Should().Be(PayoutSettlementWorkItem.StateManualReconciliation);
        item.LastErrorCode.Should().Be("poll_deadline_exceeded");
        item.GatewayReference.Should().Be("payos-123");
        item.NextAttemptAtUtc.Should().BeNull();
        item.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void TerminalStates_AcceptNoFurtherTransitions()
    {
        var (completed, ct) = ClaimedForSubmit();
        completed.Complete(ct, Now);
        AssertFrozen(completed);

        var (reconcile, rt) = ClaimedForSubmit();
        reconcile.RequireReconciliation(rt, "lookup_failed");
        AssertFrozen(reconcile);

        static void AssertFrozen(PayoutSettlementWorkItem item)
        {
            var state = item.State;
            var token = Guid.NewGuid();
            item.TryClaim(Now.AddDays(5), token, Lease).Should().BeFalse();
            item.RecordSubmitted(token, "x", Now, PollWindow, Now).Should().BeFalse();
            item.ScheduleSubmitRetry(token, "x", Now).Should().BeFalse();
            item.RecordPoll(token, Now, Now).Should().BeFalse();
            item.Complete(token, Now).Should().BeFalse();
            item.RequireReconciliation(token, "x").Should().BeFalse();
            item.RecoverExpiredLease(Now.AddDays(5), 3).Should().BeFalse();
            item.State.Should().Be(state);
        }
    }

    [Fact]
    public void RecoverExpiredLease_OnCrashedSubmit_ReturnsToPendingForALookupFirstRetry()
    {
        var (item, _) = ClaimedForSubmit();

        item.RecoverExpiredLease(Now + Lease - TimeSpan.FromSeconds(1), maxSubmitAttempts: 3).Should().BeFalse("lease still valid");
        item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitting);

        item.RecoverExpiredLease(Now + Lease, maxSubmitAttempts: 3).Should().BeTrue();

        item.State.Should().Be(PayoutSettlementWorkItem.StatePending);
        item.LeaseToken.Should().BeNull();
        item.NextAttemptAtUtc.Should().Be(Now + Lease);
        item.LastErrorCode.Should().Be(PayoutSettlementWorkItem.LeaseExpiredCode);
        item.AttemptCount.Should().Be(1);
    }

    [Fact]
    public void RecoverExpiredLease_WhenSubmitAttemptsAreExhausted_GoesToManualReconciliation()
    {
        var (item, token) = ClaimedForSubmit();
        item.ScheduleSubmitRetry(token, "gateway_unavailable", Now);
        item.TryClaim(Now, Guid.NewGuid(), Lease);
        item.ScheduleSubmitRetry(item.LeaseToken!.Value, "gateway_unavailable", Now);
        item.TryClaim(Now, Guid.NewGuid(), Lease);
        item.AttemptCount.Should().Be(3);

        item.RecoverExpiredLease(Now + Lease, maxSubmitAttempts: 3).Should().BeTrue();

        item.State.Should().Be(PayoutSettlementWorkItem.StateManualReconciliation);
        item.LastErrorCode.Should().Be(PayoutSettlementWorkItem.LeaseExpiredCode);
    }

    [Fact]
    public void RecoverExpiredLease_OnCrashedPoll_KeepsSubmittedAndMakesItDueAgain()
    {
        var (item, _) = SubmittedItem();
        item.TryClaim(Now.AddSeconds(30), Guid.NewGuid(), Lease);

        item.RecoverExpiredLease(Now.AddSeconds(30) + Lease, maxSubmitAttempts: 3).Should().BeTrue();

        item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
        item.LeaseToken.Should().BeNull();
        item.NextAttemptAtUtc.Should().Be(Now.AddSeconds(30) + Lease);
        item.GatewayReference.Should().Be("payos-123");
    }

    private static (PayoutSettlementWorkItem Item, Guid Token) ClaimedForSubmit()
    {
        var item = PayoutSettlementWorkItem.Create(42, Now);
        var token = Guid.NewGuid();
        item.TryClaim(Now, token, Lease).Should().BeTrue();
        return (item, token);
    }

    private static (PayoutSettlementWorkItem Item, Guid Token) SubmittedItem()
    {
        var (item, token) = ClaimedForSubmit();
        item.RecordSubmitted(token, "payos-123", Now, PollWindow, Now.AddSeconds(30)).Should().BeTrue();
        return (item, token);
    }
}
