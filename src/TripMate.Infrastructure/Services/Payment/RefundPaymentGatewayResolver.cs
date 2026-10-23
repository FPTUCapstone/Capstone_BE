using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services.Payment;

/// <summary>
/// Keeps refund routing bound to the recorded original transaction provider. New checkout
/// configuration is deliberately irrelevant here: a later provider switch must not reject
/// historic refunds or send them to a different channel.
/// </summary>
public sealed class RefundPaymentGatewayResolver(
    MockPaymentGateway mockGateway,
    PayOsPaymentGateway payOsGateway) : IRefundPaymentGatewayResolver
{
    public IPaymentGateway? Resolve(PaymentGateway gateway) => gateway switch
    {
        PaymentGateway.Mock => mockGateway,
        PaymentGateway.PayOS => payOsGateway,
        _ => null,
    };
}