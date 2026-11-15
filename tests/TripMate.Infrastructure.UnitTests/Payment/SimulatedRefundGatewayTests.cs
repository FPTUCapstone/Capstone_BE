using FluentAssertions;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services.Payment;

namespace TripMate.Infrastructure.UnitTests.Payment;

// UC-41/UC-42/UC-67 SRS-alignment spec v1.2, Q4 (owner decision 13/11/2026: "giả lập nhưng như
// thật"): refunds to the Traveler are simulated for every original provider, the same way
// SimulatedPayoutTransferGateway already simulates the Tour Operator payout side.
public sealed class SimulatedRefundGatewayTests
{
    [Theory]
    [InlineData(PaymentGateway.Mock)]
    [InlineData(PaymentGateway.PayOS)]
    public async Task SubmitRefundAsync_AlwaysSimulatesAcceptance(PaymentGateway originalProvider)
    {
        var gateway = new SimulatedRefundGateway(originalProvider);

        var result = await gateway.SubmitRefundAsync(
            new RefundSubmissionRequest(RefundId: 42, BookingId: 7, Amount: 100_000m, Currency: "VND",
                OriginalGatewayTransactionReference: "gw-ref", Reason: "Traveler cancelled",
                IdempotencyKey: "tripmate-refund-42"),
            CancellationToken.None);

        result.IsAccepted.Should().BeTrue();
        result.GatewayReference.Should().Be("SIM-REFUND-42");
        result.RequiresManualReview.Should().BeFalse();
    }

    [Theory]
    [InlineData(PaymentGateway.Mock)]
    [InlineData(PaymentGateway.PayOS)]
    public void Gateway_ReportsTheOriginalProvider_ForTraceability(PaymentGateway originalProvider)
    {
        // BR-76: a refund is traced back to the original payment channel even though the
        // submission itself is simulated.
        var gateway = new SimulatedRefundGateway(originalProvider);

        gateway.Gateway.Should().Be(originalProvider);
    }

    [Fact]
    public async Task CreatePaymentLinkAsync_IsNotSupported()
    {
        var gateway = new SimulatedRefundGateway(PaymentGateway.Mock);

        Func<Task> act = () => gateway.CreatePaymentLinkAsync(
            new PaymentLinkRequest(BookingId: 1, OrderCode: 1, Amount: 100_000m, Currency: "VND",
                Description: "n/a", ExpiresAtUtc: null),
            CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }
}