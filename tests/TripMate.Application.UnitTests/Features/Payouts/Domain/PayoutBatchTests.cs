using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Payouts.Domain;

// DAILY-PAYOUT-TRANSFER, Task 3/9 (spec v6, PD-10): batch state machine and settlement cascade.
public sealed class PayoutBatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

    private static PayoutBatch CreateBatchWithOneSettlement(decimal payoutAmount = 1_400_000m)
    {
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 8), tourOperatorUserId: 7, T0);
        var settlement = PayoutSettlement.Create(
            batchId: 1, bookingId: 1, tourOperatorUserId: 7, runDate: new DateOnly(2026, 11, 8),
            kind: PayoutSettlementKind.Advance, grossAmount: 1_400_000m, commissionRate: 10m,
            commissionAmount: 0m, netAmount: 1_400_000m, debtOffsetAmount: 0m,
            payoutAmount: payoutAmount, createdAtUtc: T0);
        batch.AddSettlement(settlement);
        return batch;
    }

    [Fact]
    public void Create_StartsScheduledWithZeroTotal()
    {
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 8), 7, T0);

        batch.Status.Should().Be(PayoutSettlementStatus.Scheduled);
        batch.TotalAmount.Should().Be(0m);
        batch.AttemptCount.Should().Be(0);
    }

    [Fact]
    public void AddSettlement_AccumulatesTotalAmount()
    {
        var batch = CreateBatchWithOneSettlement(1_400_000m);

        batch.TotalAmount.Should().Be(1_400_000m);
        batch.Settlements.Should().ContainSingle();
    }

    [Fact]
    public void MarkProcessing_FromScheduled_Succeeds_AndCascadesToSettlements()
    {
        var batch = CreateBatchWithOneSettlement();
        var t1 = T0.AddMinutes(1);

        batch.MarkProcessing(t1);

        batch.Status.Should().Be(PayoutSettlementStatus.Processing);
        batch.AttemptCount.Should().Be(1);
        batch.LastAttemptAtUtc.Should().Be(t1);
        batch.Settlements.Single().Status.Should().Be(PayoutSettlementStatus.Processing);
    }

    [Fact]
    public void MarkProcessing_FromProcessing_Throws()
    {
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);

        var act = () => batch.MarkProcessing(T0);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkSettled_FromProcessing_Succeeds_AndSetsTransferReferenceOnSettlements()
    {
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);

        batch.MarkSettled("SIM-2026-11-08-7-1", T0.AddMinutes(2));

        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.TransferReference.Should().Be("SIM-2026-11-08-7-1");
        batch.Settlements.Single().Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.Settlements.Single().TransferReference.Should().Be("SIM-2026-11-08-7-1");
    }

    [Fact]
    public void MarkSettled_WithBlankReference_Throws()
    {
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);

        var act = () => batch.MarkSettled("  ", T0);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkFailed_FromProcessing_Succeeds()
    {
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);

        batch.MarkFailed(T0.AddHours(1));

        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.Settlements.Single().Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
    }

    [Fact]
    public void MarkFailed_FromScheduled_Succeeds()
    {
        // SRS 3.10.5, PD-7a: a lookup on a resting (Scheduled) batch can reveal it was Rejected,
        // without a fresh send ever having been made in this call.
        var batch = CreateBatchWithOneSettlement();

        batch.MarkFailed(T0);

        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
    }

    [Fact]
    public void MarkSettled_FromScheduled_Succeeds_WithoutConsumingAnAttempt()
    {
        // SRS 3.10.5, PD-7a: a lookup on a resting batch can reveal a past send already succeeded.
        var batch = CreateBatchWithOneSettlement();

        batch.MarkSettled("SIM-ref", T0);

        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.AttemptCount.Should().Be(0);
    }

    [Fact]
    public void MarkFailedWithoutAttempt_FromScheduled_Succeeds_WithoutConsumingAnAttempt()
    {
        // SRS 3.10.5, 1.b1 (MSG146): the Tour Operator's bank account is missing/incomplete —
        // discovered before any gateway call is ever made.
        var batch = CreateBatchWithOneSettlement();

        batch.MarkFailedWithoutAttempt(T0);

        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.AttemptCount.Should().Be(0);
        batch.Settlements.Single().Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
    }

    [Fact]
    public void MarkFailedWithoutAttempt_FromProcessing_Throws()
    {
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);

        var act = () => batch.MarkFailedWithoutAttempt(T0);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ReturnToScheduledAfterFailedAttempt_FromProcessing_RestsAsScheduled()
    {
        // SRS 3.10.5, abnormal 3.a1: a failed attempt rests as Scheduled between hourly retries,
        // keeping the attempt already recorded by MarkProcessing.
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);

        batch.ReturnToScheduledAfterFailedAttempt(T0.AddMinutes(5));

        batch.Status.Should().Be(PayoutSettlementStatus.Scheduled);
        batch.AttemptCount.Should().Be(1);
        batch.Settlements.Single().Status.Should().Be(PayoutSettlementStatus.Scheduled);
    }

    [Fact]
    public void ReturnToScheduledAfterFailedAttempt_FromScheduled_Throws()
    {
        var batch = CreateBatchWithOneSettlement();

        var act = () => batch.ReturnToScheduledAfterFailedAttempt(T0);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Retry_FromSettlementFailed_MovesToProcessingAndIncrementsAttempt()
    {
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);
        batch.MarkFailed(T0.AddHours(1));

        batch.Retry(T0.AddHours(2));

        batch.Status.Should().Be(PayoutSettlementStatus.Processing);
        batch.AttemptCount.Should().Be(2);
    }

    [Fact]
    public void Retry_FromScheduled_Throws()
    {
        var batch = CreateBatchWithOneSettlement();

        var act = () => batch.Retry(T0);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkResolved_FromSettlementFailed_RequiresReason_AndNeverSetsATransferReference()
    {
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);
        batch.MarkFailed(T0.AddHours(1));

        var act = () => batch.MarkResolved("", T0.AddHours(2));
        act.Should().Throw<ArgumentException>();

        batch.MarkResolved("Operator confirmed transfer received out of band", T0.AddHours(2));
        batch.Status.Should().Be(PayoutSettlementStatus.Resolved);
        batch.TransferReference.Should().BeNull("UC-73 [Mark as Resolved] never moves money (PD-10)");
        batch.Settlements.Single().Status.Should().Be(PayoutSettlementStatus.Resolved);
    }

    [Fact]
    public void MarkResolved_FromProcessing_Throws()
    {
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);

        var act = () => batch.MarkResolved("reason", T0);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkSettledWithoutTransfer_WithZeroTotal_SettlesWithoutAGatewayCall()
    {
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 8), 7, T0);
        var settlement = PayoutSettlement.Create(1, 1, 7, new DateOnly(2026, 11, 8), PayoutSettlementKind.Advance,
            1_400_000m, 10m, 0m, 1_400_000m, debtOffsetAmount: 1_400_000m, payoutAmount: 0m, createdAtUtc: T0);
        batch.AddSettlement(settlement);

        batch.MarkSettledWithoutTransfer(T0.AddMinutes(1));

        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.TransferReference.Should().BeNull();
        batch.Settlements.Single().Status.Should().Be(PayoutSettlementStatus.Settled);
    }

    [Fact]
    public void MarkSettledWithoutTransfer_WithNonZeroTotal_Throws()
    {
        var batch = CreateBatchWithOneSettlement(1_400_000m);

        var act = () => batch.MarkSettledWithoutTransfer(T0);

        act.Should().Throw<InvalidOperationException>();
    }
}
