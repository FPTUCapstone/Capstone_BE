using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.Refunds;

// UC-42 (SRS 3.8.4.3, spec v1.2): the refund state machine between a Traveler's cancellation
// (UC-41) and the Traveler actually being paid (UC-67) — PendingOperatorConfirmation ->
// AwaitingPartnerReturn -> ReturnReported -> ReturnReceived -> Pending (ready for UC-67), with a
// zero-share shortcut straight to Pending, and the Escalated/Reconciliation terminal review states.
public sealed class RefundStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private static Refund CreatePendingConfirmation(decimal amount = 100_000m) =>
        Refund.CreatePendingOperatorConfirmation(41, 51, 61, amount, "Traveler cancelled", RefundInitiator.System, Now);

    [Fact]
    public void CreatePendingOperatorConfirmation_StartsInThatStatus()
    {
        var refund = CreatePendingConfirmation();

        Assert.Equal(RefundStatus.PendingOperatorConfirmation, refund.Status);
        Assert.Null(refund.NextAttemptAtUtc); // not yet eligible for RefundProcessor (G1/BR-143)
    }

    [Fact]
    public void Confirm_WithNonZeroShare_MovesToAwaitingPartnerReturnWithDeadline()
    {
        var refund = CreatePendingConfirmation();
        var deadline = Now.AddDays(5);

        refund.Confirm(operatorReturnShare: 70_000m, returnDeadlineAtUtc: deadline, nowUtc: Now);

        Assert.Equal(RefundStatus.AwaitingPartnerReturn, refund.Status);
        Assert.Equal(70_000m, refund.OperatorReturnShare);
        Assert.Equal(deadline, refund.ReturnDeadlineAtUtc);
        Assert.Equal(Now, refund.OperatorConfirmedAtUtc);
        Assert.Null(refund.NextAttemptAtUtc); // still not eligible for UC-67
    }

    [Fact]
    public void Confirm_WithZeroShare_SkipsStraightToPendingReadyForUc67()
    {
        // BR-139, SRS 3.8.4.3 abnormal 4.b1: no settlement transferred yet, so the operator's
        // share is 0 — the refund is paid as soon as the request is confirmed, no partner return.
        var refund = CreatePendingConfirmation();

        refund.Confirm(operatorReturnShare: 0m, returnDeadlineAtUtc: null, nowUtc: Now);

        Assert.Equal(RefundStatus.Pending, refund.Status);
        Assert.Equal(0m, refund.OperatorReturnShare);
        Assert.Null(refund.ReturnDeadlineAtUtc);
        Assert.Equal(Now, refund.NextAttemptAtUtc); // now eligible for RefundProcessor
    }

    [Fact]
    public void Confirm_WithNonZeroShareAndNoDeadline_Throws()
    {
        var refund = CreatePendingConfirmation();

        Assert.Throws<ArgumentException>(() => refund.Confirm(70_000m, returnDeadlineAtUtc: null, nowUtc: Now));
    }

    [Fact]
    public void Confirm_WhenNotPendingOperatorConfirmation_Throws()
    {
        var refund = CreatePendingConfirmation();
        refund.Confirm(0m, null, Now);

        Assert.Throws<InvalidOperationException>(() => refund.Confirm(0m, null, Now));
    }

    [Fact]
    public void Confirm_WithNegativeShare_Throws()
    {
        var refund = CreatePendingConfirmation();

        Assert.Throws<ArgumentOutOfRangeException>(() => refund.Confirm(-1m, Now.AddDays(3), Now));
    }

    [Fact]
    public void ReportReturn_FromAwaitingPartnerReturn_MovesToReturnReportedWithReference()
    {
        var refund = CreatePendingConfirmation();
        refund.Confirm(70_000m, Now.AddDays(3), Now);
        var reportedAt = Now.AddHours(5);

        refund.ReportReturn("VCB-20261120-001", reportedAt);

        Assert.Equal(RefundStatus.ReturnReported, refund.Status);
        Assert.Equal("VCB-20261120-001", refund.ReturnReference);
        Assert.Equal(reportedAt, refund.ReturnReportedAtUtc);
    }

    [Fact]
    public void ReportReturn_WithBlankReference_Throws() // MSG172
    {
        var refund = CreatePendingConfirmation();
        refund.Confirm(70_000m, Now.AddDays(3), Now);

        Assert.Throws<ArgumentException>(() => refund.ReportReturn("   ", Now));
    }

    [Fact]
    public void ReportReturn_WhenNotAwaitingPartnerReturn_Throws()
    {
        var refund = CreatePendingConfirmation();

        Assert.Throws<InvalidOperationException>(() => refund.ReportReturn("VCB-1", Now));
    }

    [Fact]
    public void RecordReturnReceived_FromReturnReported_MovesToPendingReadyForUc67()
    {
        var refund = CreatePendingConfirmation();
        refund.Confirm(70_000m, Now.AddDays(3), Now);
        refund.ReportReturn("VCB-1", Now.AddHours(1));
        var receivedAt = Now.AddDays(2);

        refund.RecordReturnReceived(receivedAt);

        Assert.Equal(RefundStatus.Pending, refund.Status);
        Assert.Equal(receivedAt, refund.ReturnReceivedAtUtc);
        Assert.Equal(receivedAt, refund.NextAttemptAtUtc); // now eligible for RefundProcessor
    }

    [Fact]
    public void RecordReturnReceived_WhenNotReturnReported_Throws()
    {
        var refund = CreatePendingConfirmation();
        refund.Confirm(70_000m, Now.AddDays(3), Now);

        Assert.Throws<InvalidOperationException>(() => refund.RecordReturnReceived(Now));
    }

    [Fact]
    public void Escalate_FromProcessing_MovesToEscalated() // BR-161, after retries exhausted
    {
        var refund = CreatePendingConfirmation();
        refund.Confirm(0m, null, Now);
        refund.TryStartAttempt(Now);

        refund.Escalate("payment.refund.retries_exhausted", "Gateway unreachable after 4 attempts.");

        Assert.Equal(RefundStatus.Escalated, refund.Status);
        Assert.Equal("payment.refund.retries_exhausted", refund.LastErrorCode);
        Assert.Null(refund.NextAttemptAtUtc);
    }

    [Fact]
    public void Escalate_WhenNotProcessing_Throws()
    {
        var refund = CreatePendingConfirmation();

        Assert.Throws<InvalidOperationException>(() => refund.Escalate("code", null));
    }

    [Fact]
    public void CreateReconciliation_StartsInReconciliationStatus() // UC-41 6.b1: amount mismatch
    {
        var refund = Refund.CreateReconciliation(41, 51, 61, 100_000m, "Amount mismatch", RefundInitiator.System, Now);

        Assert.Equal(RefundStatus.Reconciliation, refund.Status);
        Assert.Null(refund.NextAttemptAtUtc);
    }
}