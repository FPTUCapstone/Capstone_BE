using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Admin.PayoutSettlements.Domain;

public sealed class PayoutSettlementTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AuthorizeSettlement_FromApprovedForSettlement_MovesToProcessingAndStoresAuthorizerAndBin()
    {
        var payout = ApprovedPayout();

        var changed = payout.AuthorizeSettlement(9, Now, "970436");

        changed.Should().BeTrue();
        payout.Status.Should().Be(Payout.StatusSettlementProcessing);
        payout.SettlementAuthorizedBy.Should().Be(9);
        payout.SettlementAuthorizedAtUtc.Should().Be(Now);
        payout.BankBin.Should().Be("970436");
        payout.ReviewedBy.Should().Be(5, "the Staff review stays untouched");
        payout.GatewayAttemptCount.Should().Be(0);
    }

    [Theory]
    [InlineData(Payout.StatusPendingReview)]
    [InlineData(Payout.StatusSettlementProcessing)]
    [InlineData(Payout.StatusSettled)]
    [InlineData(Payout.StatusSettlementFailed)]
    [InlineData(Payout.StatusRejected)]
    [InlineData(Payout.StatusCancelled)]
    public void AuthorizeSettlement_FromAnyOtherStatus_ReturnsFalseAndChangesNothing(string status)
    {
        var payout = PayoutInStatus(status);

        var changed = payout.AuthorizeSettlement(9, Now, "970436");

        changed.Should().BeFalse();
        payout.Status.Should().Be(status);
        payout.SettlementAuthorizedBy.Should().Be(status is Payout.StatusSettlementProcessing
            or Payout.StatusSettled or Payout.StatusSettlementFailed ? 9 : null);
        payout.BankBin.Should().Be(status is Payout.StatusSettlementProcessing
            or Payout.StatusSettled or Payout.StatusSettlementFailed ? "970436" : null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AuthorizeSettlement_WithInvalidAdministrator_Throws(long administratorId)
    {
        var payout = ApprovedPayout();

        var act = () => payout.AuthorizeSettlement(administratorId, Now, "970436");

        act.Should().Throw<ArgumentOutOfRangeException>();
        payout.Status.Should().Be(Payout.StatusApprovedForSettlement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("97043")]
    [InlineData("9704366")]
    [InlineData("97043A")]
    [InlineData("97 436")]
    public void AuthorizeSettlement_WithMalformedBin_ThrowsAndChangesNothing(string? bin)
    {
        var payout = ApprovedPayout();

        var act = () => payout.AuthorizeSettlement(9, Now, bin!);

        act.Should().Throw<ArgumentException>();
        payout.Status.Should().Be(Payout.StatusApprovedForSettlement);
        payout.BankBin.Should().BeNull();
    }

    [Fact]
    public void RecordGatewayAttempt_CountsEveryCallAndStoresLatestInstant()
    {
        var payout = ProcessingPayout();

        payout.RecordGatewayAttempt(Now.AddMinutes(1));
        payout.RecordGatewayAttempt(Now.AddMinutes(2));

        payout.GatewayAttemptCount.Should().Be(2);
        payout.LastGatewayAttemptAtUtc.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void RecordGatewayAttempt_OutsideProcessing_Throws()
    {
        var act = () => ApprovedPayout().RecordGatewayAttempt(Now);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetGatewayReference_StoresTrimmedReference_AndRejectsBlankOrTooLong()
    {
        var payout = ProcessingPayout();

        payout.SetGatewayReference("  payos-123 ");
        payout.GatewayReference.Should().Be("payos-123");

        var blank = () => payout.SetGatewayReference("  ");
        blank.Should().Throw<ArgumentException>();
        var tooLong = () => payout.SetGatewayReference(new string('x', Payout.GatewayReferenceMaxLength + 1));
        tooLong.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkSettled_AfterAttemptAndReference_MovesToSettledAndKeepsAttemptEvidence()
    {
        var payout = ProcessingPayout();
        payout.RecordGatewayAttempt(Now.AddMinutes(1));
        payout.SetGatewayReference("payos-123");
        payout.RecordGatewayAttempt(Now.AddMinutes(30));

        var changed = payout.MarkSettled();

        changed.Should().BeTrue();
        payout.Status.Should().Be(Payout.StatusSettled);
        payout.GatewayReference.Should().Be("payos-123");
        payout.GatewayFailureReason.Should().BeNull();
        payout.GatewayAttemptCount.Should().Be(2);
        payout.LastGatewayAttemptAtUtc.Should().Be(Now.AddMinutes(30));
    }

    [Fact]
    public void MarkSettled_WithoutGatewayReferenceOrAttempt_Throws()
    {
        var withoutReference = ProcessingPayout();
        withoutReference.RecordGatewayAttempt(Now);
        var noReference = () => withoutReference.MarkSettled();
        noReference.Should().Throw<InvalidOperationException>();

        var withoutAttempt = ProcessingPayout();
        withoutAttempt.SetGatewayReference("payos-123");
        var noAttempt = () => withoutAttempt.MarkSettled();
        noAttempt.Should().Throw<InvalidOperationException>();

        withoutReference.Status.Should().Be(Payout.StatusSettlementProcessing);
        withoutAttempt.Status.Should().Be(Payout.StatusSettlementProcessing);
    }

    [Fact]
    public void MarkSettlementFailed_AfterAttempt_StoresSafeReasonAndMovesToFailed()
    {
        var payout = ProcessingPayout();
        payout.RecordGatewayAttempt(Now.AddMinutes(1));

        var changed = payout.MarkSettlementFailed(" gateway_rejected ");

        changed.Should().BeTrue();
        payout.Status.Should().Be(Payout.StatusSettlementFailed);
        payout.GatewayFailureReason.Should().Be("gateway_rejected");
        payout.GatewayAttemptCount.Should().Be(1);
    }

    [Fact]
    public void MarkSettlementFailed_WithoutAttemptOrWithInvalidReason_Throws()
    {
        var noAttempt = ProcessingPayout();
        var withoutAttempt = () => noAttempt.MarkSettlementFailed("gateway_rejected");
        withoutAttempt.Should().Throw<InvalidOperationException>();

        var attempted = ProcessingPayout();
        attempted.RecordGatewayAttempt(Now);
        var blank = () => attempted.MarkSettlementFailed(" ");
        blank.Should().Throw<ArgumentException>();
        var tooLong = () => attempted.MarkSettlementFailed(new string('x', Payout.GatewayFailureReasonMaxLength + 1));
        tooLong.Should().Throw<ArgumentException>();
        attempted.Status.Should().Be(Payout.StatusSettlementProcessing);
    }

    [Theory]
    [InlineData(Payout.StatusApprovedForSettlement)]
    [InlineData(Payout.StatusSettled)]
    [InlineData(Payout.StatusSettlementFailed)]
    public void Outcomes_OutsideProcessing_AreRejectedWithoutChange(string status)
    {
        var payout = PayoutInStatus(status);
        var before = payout.Status;

        payout.MarkSettled().Should().BeFalse();
        payout.MarkSettlementFailed("gateway_rejected").Should().BeFalse();

        payout.Status.Should().Be(before);
    }

    [Fact]
    public void SettledAndFailed_AreTerminalForFurtherOutcomes()
    {
        var settled = PayoutInStatus(Payout.StatusSettled);
        settled.MarkSettlementFailed("late_failure").Should().BeFalse();
        settled.Status.Should().Be(Payout.StatusSettled);

        var failed = PayoutInStatus(Payout.StatusSettlementFailed);
        failed.MarkSettled().Should().BeFalse();
        failed.Status.Should().Be(Payout.StatusSettlementFailed);
    }

    internal static Payout ApprovedPayout()
    {
        var payout = Payout.Create(
            payoutCode: "PO-202610-000001",
            operatorUserId: 7,
            months: [new PayoutMonthTotal(new DateOnly(2026, 10, 1), 2, 6, 3_000_000m, 0m, 300_000m, 270_000m, 2_430_000m)],
            cutOffAtUtc: Now,
            adjustmentsApplied: 0m,
            minimumAmountChecked: 1_000_000m,
            bankName: "Vietcombank",
            accountHolderName: "Nguyen Van A",
            accountNumberMasked: "****1234",
            accountNumberProtected: "protected:1029384756",
            requestedAtUtc: Now.AddDays(-3));
        payout.Approve(5, Now.AddDays(-1));
        return payout;
    }

    internal static Payout ProcessingPayout()
    {
        var payout = ApprovedPayout();
        payout.AuthorizeSettlement(9, Now, "970436");
        return payout;
    }

    private static Payout PayoutInStatus(string status)
    {
        switch (status)
        {
            case Payout.StatusApprovedForSettlement:
                return ApprovedPayout();
            case Payout.StatusSettlementProcessing:
                return ProcessingPayout();
            case Payout.StatusSettled:
            {
                var settled = ProcessingPayout();
                settled.RecordGatewayAttempt(Now.AddMinutes(1));
                settled.SetGatewayReference("payos-123");
                settled.MarkSettled();
                return settled;
            }

            case Payout.StatusSettlementFailed:
            {
                var failed = ProcessingPayout();
                failed.RecordGatewayAttempt(Now.AddMinutes(1));
                failed.MarkSettlementFailed("gateway_rejected");
                return failed;
            }

            case Payout.StatusPendingReview:
                return Payout.Create(
                    "PO-202610-000002", 7,
                    [new PayoutMonthTotal(new DateOnly(2026, 10, 1), 1, 1, 1_000_000m, 0m, 0m, 100_000m, 900_000m)],
                    Now, 0m, 1_000_000m, "Vietcombank", "A", "****1", "protected:x", Now);
            case Payout.StatusRejected:
            {
                var rejected = PayoutInStatus(Payout.StatusPendingReview);
                rejected.Reject(5, "Beneficiary mismatch", Now);
                return rejected;
            }

            case Payout.StatusCancelled:
            {
                var cancelled = PayoutInStatus(Payout.StatusPendingReview);
                cancelled.Cancel(Now);
                return cancelled;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }
    }
}
