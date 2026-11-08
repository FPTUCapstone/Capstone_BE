using FluentAssertions;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Payouts;

// DAILY-PAYOUT-TRANSFER, Task 5 (PD-6, PD-7a): the simulated gateway adapter.
public sealed class SimulatedPayoutTransferGatewayTests
{
    [Fact]
    public async Task SendAsync_WithSucceedingScenario_ReturnsSucceeded()
    {
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        var result = await gateway.SendAsync("SIM-20261108-7-1", 7, 1_800_000m, CancellationToken.None);

        result.Outcome.Should().Be(PayoutTransferOutcome.Succeeded);
        result.GatewayReference.Should().Be("SIM-20261108-7-1");
    }

    [Theory]
    [InlineData(PayoutTransferOutcome.Failed)]
    [InlineData(PayoutTransferOutcome.Timeout)]
    [InlineData(PayoutTransferOutcome.Rejected)]
    public async Task SendAsync_WithNonSucceedingScenario_ReturnsThatOutcome(PayoutTransferOutcome outcome)
    {
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(outcome));

        var result = await gateway.SendAsync("SIM-ref", 7, 1_000_000m, CancellationToken.None);

        result.Outcome.Should().Be(outcome);
    }

    [Fact]
    public async Task LookupAsync_DelegatesToScenario()
    {
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        var result = await gateway.LookupAsync("SIM-ref", CancellationToken.None);

        result.Outcome.Should().Be(PayoutTransferOutcome.Succeeded);
    }

    [Fact]
    public void CreateReference_IsStableForTheSameRunDateOperatorAndBatch()
    {
        var a = PayoutGatewayReference.Create(new DateOnly(2026, 11, 8), tourOperatorUserId: 7, batchId: 1);
        var b = PayoutGatewayReference.Create(new DateOnly(2026, 11, 8), tourOperatorUserId: 7, batchId: 1);
        var different = PayoutGatewayReference.Create(new DateOnly(2026, 11, 8), tourOperatorUserId: 8, batchId: 1);

        a.Should().Be(b);
        a.Should().NotBe(different);
        a.Should().StartWith("SIM-20261108-7-1");
    }

    [Fact]
    public async Task SequenceScenario_ReturnsEachOutcomeInOrderThenRepeatsLast()
    {
        var scenario = new SequenceOutcomeScenario(PayoutTransferOutcome.Timeout, PayoutTransferOutcome.Failed, PayoutTransferOutcome.Succeeded);
        var gateway = new SimulatedPayoutTransferGateway(scenario);

        (await gateway.SendAsync("ref", 1, 1m, CancellationToken.None)).Outcome.Should().Be(PayoutTransferOutcome.Timeout);
        (await gateway.SendAsync("ref", 1, 1m, CancellationToken.None)).Outcome.Should().Be(PayoutTransferOutcome.Failed);
        (await gateway.SendAsync("ref", 1, 1m, CancellationToken.None)).Outcome.Should().Be(PayoutTransferOutcome.Succeeded);
        (await gateway.SendAsync("ref", 1, 1m, CancellationToken.None)).Outcome.Should().Be(PayoutTransferOutcome.Succeeded);
    }
}
