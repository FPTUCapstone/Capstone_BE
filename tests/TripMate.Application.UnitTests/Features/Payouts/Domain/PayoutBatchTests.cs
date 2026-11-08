using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Payouts.Domain;

// DAILY-PAYOUT-TRANSFER, Task 3 (PD-10): batch state machine and settlement cascade.
public sealed class PayoutBatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

    private static PayoutBatch CreateBatchWithOneSettlement(decimal payoutAmount = 1_800_000m)
    {
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 8), tourOperatorUserId: 7, T0);
        var settlement = PayoutSettlement.Create(
            batchId: 1, bookingId: 1, tourOperatorUserId: 7, runDate: new DateOnly(2026, 11, 8),
            kind: PayoutSettlementKind.Main, grossAmount: 2_000_000m, commissionRate: 10m,
            commissionAmount: 200_000m, netAmount: 1_800_000m, debtOffsetAmount: 0m,
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
        var batch = CreateBatchWithOneSettlement(1_800_000m);

        batch.TotalAmount.Should().Be(1_800_000m);
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
    public void MarkSettled_FromScheduled_Throws()
    {
        var batch = CreateBatchWithOneSettlement();

        var act = () => batch.MarkSettled("ref", T0);

        act.Should().Throw<InvalidOperationException>();
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
    public void MarkSettledManually_FromSettlementFailed_RequiresReference()
    {
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);
        batch.MarkFailed(T0.AddHours(1));

        var act = () => batch.MarkSettledManually("", T0.AddHours(2));
        act.Should().Throw<ArgumentException>();

        batch.MarkSettledManually("MANUAL-REF-1", T0.AddHours(2));
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.Settlements.Single().TransferReference.Should().Be("MANUAL-REF-1");
    }

    [Fact]
    public void MarkCarriedOver_FromSettlementFailed_Succeeds_KeepsAmount()
    {
        var batch = CreateBatchWithOneSettlement(1_800_000m);
        batch.MarkProcessing(T0);
        batch.MarkFailed(T0.AddHours(1));

        batch.MarkCarriedOver(T0.AddHours(2));

        batch.Status.Should().Be(PayoutSettlementStatus.CarriedOver);
        batch.TotalAmount.Should().Be(1_800_000m, "PC-02: the original batch keeps its own amount");
        batch.Settlements.Single().Status.Should().Be(PayoutSettlementStatus.CarriedOver);
    }

    [Fact]
    public void MarkSettledWithoutTransfer_WithZeroTotal_SettlesWithoutAGatewayCall()
    {
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 8), 7, T0);
        var settlement = PayoutSettlement.Create(1, 1, 7, new DateOnly(2026, 11, 8), PayoutSettlementKind.Main,
            1_000_000m, 10m, 100_000m, 900_000m, debtOffsetAmount: 900_000m, payoutAmount: 0m, createdAtUtc: T0);
        batch.AddSettlement(settlement);

        batch.MarkSettledWithoutTransfer(T0.AddMinutes(1));

        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.TransferReference.Should().BeNull();
        batch.Settlements.Single().Status.Should().Be(PayoutSettlementStatus.Settled);
    }

    [Fact]
    public void MarkSettledWithoutTransfer_WithNonZeroTotal_Throws()
    {
        var batch = CreateBatchWithOneSettlement(1_800_000m);

        var act = () => batch.MarkSettledWithoutTransfer(T0);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkCarriedOver_FromProcessing_Throws()
    {
        var batch = CreateBatchWithOneSettlement();
        batch.MarkProcessing(T0);

        var act = () => batch.MarkCarriedOver(T0);

        act.Should().Throw<InvalidOperationException>();
    }
}
