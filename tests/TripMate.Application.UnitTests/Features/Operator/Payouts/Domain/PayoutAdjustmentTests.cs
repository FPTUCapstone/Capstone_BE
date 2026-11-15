using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Operator.Payouts.Domain;

public sealed class PayoutAdjustmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithNegativeAmount_Succeeds()
    {
        var adjustment = PayoutAdjustment.Create(operatorUserId: 7, sourceRefundId: 501, amount: -300_000m, createdAtUtc: Now);

        adjustment.Amount.Should().Be(-300_000m);
        adjustment.IsOutstanding.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_000)]
    public void Create_WithNonNegativeAmount_Throws(decimal amount)
    {
        var act = () => PayoutAdjustment.Create(7, 501, amount, Now);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Apply_PartialAmount_LeavesAdjustmentAppliedButNotOutstanding()
    {
        var adjustment = PayoutAdjustment.Create(7, 501, -500_000m, Now);

        adjustment.Apply(payoutId: 99, appliedAmount: -300_000m, appliedAtUtc: Now.AddDays(1));

        adjustment.IsOutstanding.Should().BeFalse();
        adjustment.AppliedPayoutId.Should().Be(99);
        adjustment.AppliedAmount.Should().Be(-300_000m);
    }

    [Fact]
    public void Apply_MoreThanOutstandingAmount_Throws()
    {
        var adjustment = PayoutAdjustment.Create(7, 501, -100_000m, Now);

        var act = () => adjustment.Apply(99, -200_000m, Now);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Apply_WhenAlreadyApplied_Throws()
    {
        var adjustment = PayoutAdjustment.Create(7, 501, -100_000m, Now);
        adjustment.Apply(99, -100_000m, Now);

        var act = () => adjustment.Apply(100, -50_000m, Now);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Revert_AfterApply_ReturnsToOutstanding()
    {
        var adjustment = PayoutAdjustment.Create(7, 501, -100_000m, Now);
        adjustment.Apply(99, -100_000m, Now);

        adjustment.Revert();

        adjustment.IsOutstanding.Should().BeTrue();
        adjustment.AppliedPayoutId.Should().BeNull();
        adjustment.AppliedAmount.Should().BeNull();
        adjustment.AppliedAtUtc.Should().BeNull();
    }
}