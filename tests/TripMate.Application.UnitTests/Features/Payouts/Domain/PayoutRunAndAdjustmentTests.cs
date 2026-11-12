using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Payouts.Domain;

// DAILY-PAYOUT-TRANSFER, Task 3 (spec v6, PD-11): PayoutRun lifecycle. Carry over
// (PayoutSettlementAdjustment) is removed in v6 — not in SRS rev10-5 (plan Task 9).
public sealed class PayoutRunAndAdjustmentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PayoutRun_Start_IsRunning()
    {
        var run = PayoutRun.Start(new DateOnly(2026, 11, 8), T0);

        run.Status.Should().Be(PayoutRun.StatusRunning);
        run.RunDate.Should().Be(new DateOnly(2026, 11, 8));
        run.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void PayoutRun_Complete_SetsStatusAndTimestamp()
    {
        var run = PayoutRun.Start(new DateOnly(2026, 11, 8), T0);

        run.Complete(T0.AddMinutes(5));

        run.Status.Should().Be(PayoutRun.StatusCompleted);
        run.CompletedAtUtc.Should().Be(T0.AddMinutes(5));
    }

    [Fact]
    public void PayoutRun_Fail_SetsStatusAndTimestamp()
    {
        var run = PayoutRun.Start(new DateOnly(2026, 11, 8), T0);

        run.Fail(T0.AddMinutes(5));

        run.Status.Should().Be(PayoutRun.StatusFailed);
    }
}
