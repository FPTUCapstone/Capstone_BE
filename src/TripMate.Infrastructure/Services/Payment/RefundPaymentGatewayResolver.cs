using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services.Payment;

/// <summary>
/// Keeps refund routing bound to the recorded original transaction provider (BR-76) — the
/// resolved gateway always reports <paramref name="gateway"/> as its own <see cref="IPaymentGateway.Gateway"/>,
/// so a later checkout-provider switch never changes which channel a historic refund is
/// attributed to. The current registration (<see cref="SimulatedRefundGateway"/>) simulates
/// acceptance for every provider (spec v1.2, Q4); swapping in a real integration later only
/// changes this one resolution, not <see cref="TripMate.Application.Features.Bookings.Refunds.RefundProcessor"/>.
/// </summary>
public sealed class RefundPaymentGatewayResolver : IRefundPaymentGatewayResolver
{
    public IPaymentGateway? Resolve(PaymentGateway gateway) => gateway switch
    {
        PaymentGateway.Mock or PaymentGateway.PayOS => new SimulatedRefundGateway(gateway),
        _ => null,
    };
}