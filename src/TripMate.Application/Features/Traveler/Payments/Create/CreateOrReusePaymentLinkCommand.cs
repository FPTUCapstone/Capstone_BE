using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Traveler.Payments.Create;

public record CreatePaymentLinkResponse(
    long PaymentId,
    long BookingId,
    string Gateway,
    string CheckoutUrl,
    string Status,
    decimal Amount,
    string Currency,
    DateTimeOffset? PaymentLinkExpiresAtUtc,
    DateTimeOffset? BookingPaymentExpiresAtUtc);

public record CreateOrReusePaymentLinkCommand(long BookingId) : IRequest<Result<CreatePaymentLinkResponse>>;