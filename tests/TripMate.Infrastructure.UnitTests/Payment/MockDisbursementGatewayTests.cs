using FluentAssertions;

using TripMate.Application.Features.Admin.PayoutSettlements.Processing;
using TripMate.Infrastructure.Services.Payment.PayoutDisbursement;

namespace TripMate.Infrastructure.UnitTests.Payment;

public sealed class MockDisbursementGatewayTests
{
    private readonly MockDisbursementGateway _gateway = new();

    [Fact]
    public async Task Submit_IsAcceptedWithAMockReferenceAndNeverTouchesTheNetwork()
    {
        var result = await _gateway.SubmitAsync(Request("PO-000001"), CancellationToken.None);

        result.Outcome.Should().Be(PayoutSubmitOutcome.Accepted);
        result.GatewayPayoutId.Should().Be("mock-payout-PO-000001");
        result.FailureCode.Should().BeNull();
    }

    [Fact]
    public async Task Status_ReportsSuccessImmediately()
    {
        var result = await _gateway.GetStatusAsync("mock-payout-PO-000001", CancellationToken.None);

        result.IsAvailable.Should().BeTrue();
        result.State.Should().Be(PayoutGatewayState.Succeeded);
    }

    [Fact]
    public async Task Lookup_ReportsNothingWasCreatedBecauseTheMockKeepsNoState()
    {
        var result = await _gateway.FindByReferenceAsync("PO-000001", CancellationToken.None);

        result.Outcome.Should().Be(PayoutLookupOutcome.NotFound);
    }

    private static PayoutSubmitRequest Request(string reference) => new(
        reference, 10_800_000m, "TripMate Payout PO-000001", "970436", "1029384756", "tripmate-payout-1");
}
