using FluentAssertions;

using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services.Payment;

namespace TripMate.Infrastructure.UnitTests.Payment;

public sealed class RefundPaymentGatewayResolverTests
{
    [Theory]
    [InlineData(PaymentGateway.Mock)]
    [InlineData(PaymentGateway.PayOS)]
    public void Resolve_ReturnsASimulatedGatewayReportingTheOriginalProvider(PaymentGateway originalProvider)
    {
        var resolver = new RefundPaymentGatewayResolver();

        var resolved = resolver.Resolve(originalProvider);

        resolved.Should().BeOfType<SimulatedRefundGateway>();
        resolved!.Gateway.Should().Be(originalProvider);
    }
}