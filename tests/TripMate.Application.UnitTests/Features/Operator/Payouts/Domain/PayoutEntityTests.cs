using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Operator.Payouts.Domain;

public sealed class PayoutEntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_SingleMonth_ComputesNetSalesAndPayableNetFromInputs()
    {
        var payout = Payout.Create(
            payoutCode: "PO-202609-000001",
            operatorUserId: 7,
            months: [new PayoutMonthTotal(new DateOnly(2026, 9, 1), 2, 6, 3_000_000m, 0m, 300_000m, 270_000m, 2_430_000m)],
            cutOffAtUtc: Now,
            adjustmentsApplied: 0m,
            minimumAmountChecked: 1_000_000m,
            bankName: "Vietcombank",
            accountHolderName: "Nguyen Van A",
            accountNumberMasked: "****1234",
            accountNumberProtected: "protected:1029384756",
            requestedAtUtc: Now);

        payout.NetSales.Should().Be(2_700_000m);
        payout.PayableNetBeforeAdjustments.Should().Be(2_430_000m);
        payout.NetAmount.Should().Be(2_430_000m);
        payout.Status.Should().Be(Payout.StatusPendingReview);
        payout.PeriodStart.Should().Be(new DateOnly(2026, 9, 1));
        payout.PeriodEnd.Should().Be(new DateOnly(2026, 9, 30));
        payout.PeriodCount.Should().Be(1);
        payout.IsCancellable.Should().BeTrue();
        payout.PeriodBreakdowns.Should().ContainSingle();
    }

    [Fact]
    public void Create_MultipleMonths_AggregatesTotalsAndSpansFullRange()
    {
        var payout = Payout.Create(
            "PO-202601-000010", 7,
            months:
            [
                new PayoutMonthTotal(new DateOnly(2025, 12, 1), 1, 2, 5_000_000m, 0m, 0m, 500_000m, 4_500_000m),
                new PayoutMonthTotal(new DateOnly(2026, 1, 1), 2, 3, 8_000_000m, 0m, 0m, 800_000m, 7_200_000m),
            ],
            cutOffAtUtc: Now, adjustmentsApplied: 0m, minimumAmountChecked: 1_000_000m,
            bankName: "VCB", accountHolderName: "A", accountNumberMasked: "****0001",
            accountNumberProtected: "protected:x", requestedAtUtc: Now);

        payout.PeriodCount.Should().Be(2);
        payout.PeriodStart.Should().Be(new DateOnly(2025, 12, 1));
        payout.PeriodEnd.Should().Be(new DateOnly(2026, 1, 31));
        payout.PaymentsReceived.Should().Be(13_000_000m);
        payout.CommissionAmount.Should().Be(1_300_000m);
        payout.NetAmount.Should().Be(11_700_000m); // 4,500,000 + 7,200,000
        payout.BookingCount.Should().Be(3);
        payout.PeriodBreakdowns.Should().HaveCount(2);
        payout.PeriodBreakdowns.Select(b => b.SettlementMonth).Should().Equal(
            new DateOnly(2025, 12, 1), new DateOnly(2026, 1, 1));
    }

    [Fact]
    public void Create_WithNoMonths_Throws()
    {
        var act = () => Payout.Create(
            "PO-X", 7, months: [], Now, adjustmentsApplied: 0m, minimumAmountChecked: 1_000_000m,
            bankName: "VCB", accountHolderName: "A", accountNumberMasked: "****0001",
            accountNumberProtected: "protected:x", requestedAtUtc: Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithNegativePayableNet_Throws()
    {
        var act = () => Payout.Create(
            "PO-202609-000002", 7,
            months: [new PayoutMonthTotal(new DateOnly(2026, 9, 1), 1, 1, 100_000m, 0m, 0m, 10_000m, 90_000m)],
            cutOffAtUtc: Now, adjustmentsApplied: 200_000m, minimumAmountChecked: 1_000_000m,
            bankName: "VCB", accountHolderName: "A", accountNumberMasked: "****0001",
            accountNumberProtected: "protected:x", requestedAtUtc: Now);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Cancel_FromPendingReview_SucceedsAndSetsCancelledStatus()
    {
        var payout = CreateValidPayout();

        var result = payout.Cancel(Now.AddHours(1));

        result.Should().BeTrue();
        payout.Status.Should().Be(Payout.StatusCancelled);
        payout.CancelledAtUtc.Should().Be(Now.AddHours(1));
        payout.IsCancellable.Should().BeFalse();
    }

    [Theory]
    [InlineData(Payout.StatusApprovedForSettlement)]
    [InlineData(Payout.StatusSettlementProcessing)]
    [InlineData(Payout.StatusSettled)]
    [InlineData(Payout.StatusSettlementFailed)]
    [InlineData(Payout.StatusRejected)]
    [InlineData(Payout.StatusCancelled)]
    public void Cancel_FromAnyNonPendingReviewStatus_Fails(string nonPendingStatus)
    {
        var payout = CreateValidPayout();
        SetStatus(payout, nonPendingStatus);

        var result = payout.Cancel(Now);

        result.Should().BeFalse();
        payout.Status.Should().Be(nonPendingStatus);
    }

    [Fact]
    public void AllStatuses_IncludesCancelled_ForOperatorInitiatedCancellation()
    {
        Payout.AllStatuses.Should().Contain(Payout.StatusCancelled);
        Payout.ActiveStatuses.Should().NotContain(Payout.StatusCancelled);
        Payout.ActiveStatuses.Should().NotContain(Payout.StatusRejected);
        Payout.ActiveStatuses.Should().Contain(Payout.StatusSettlementFailed);
    }

    private static Payout CreateValidPayout() => Payout.Create(
        "PO-202609-000003", 7,
        months: [new PayoutMonthTotal(new DateOnly(2026, 9, 1), 1, 2, 1_000_000m, 0m, 0m, 100_000m, 900_000m)],
        cutOffAtUtc: Now, adjustmentsApplied: 0m, minimumAmountChecked: 1_000_000m,
        bankName: "VCB", accountHolderName: "A", accountNumberMasked: "****0001",
        accountNumberProtected: "protected:x", requestedAtUtc: Now);

    private static void SetStatus(Payout payout, string status) =>
        typeof(Payout)
            .GetProperty(nameof(Payout.Status), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(payout, status);
}
