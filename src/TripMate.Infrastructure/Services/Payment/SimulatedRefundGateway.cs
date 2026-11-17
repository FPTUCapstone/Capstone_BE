using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services.Payment;

/// <summary>
/// Simulated refund channel (UC-41/UC-42/UC-67 SRS-alignment spec v1.2, Q4 — owner decision
/// 13/11/2026: "giả lập nhưng như thật"). Mirrors <c>SimulatedPayoutTransferGateway</c>'s own
/// reasoning for the payout side: no real original-channel refund API exists yet for either
/// provider (PayOS exposes no reversal of a completed payment, and a real integration would need
/// to invent beneficiary bank details that are not payment credentials), so <see cref="IPaymentGateway.SubmitRefundAsync"/>
/// always simulates acceptance here instead of leaving every PayOS refund permanently stuck in
/// <c>ManualReview</c>. A later real integration swaps this registration out; nothing in
/// <see cref="TripMate.Application.Features.Bookings.Refunds.RefundProcessor"/> needs to change.
/// <see cref="Gateway"/> still reports the real original provider (BR-76), for traceability only
/// — <see cref="CreatePaymentLinkAsync"/> is never called for a refund and is intentionally
/// unsupported, since this gateway exists only behind <see cref="IRefundPaymentGatewayResolver"/>.
/// </summary>
public sealed class SimulatedRefundGateway(PaymentGateway gateway) : IPaymentGateway
{
    public PaymentGateway Gateway { get; } = gateway;

    public Task<PaymentLinkResult> CreatePaymentLinkAsync(
        PaymentLinkRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            $"{nameof(SimulatedRefundGateway)} only submits refunds; it never creates a payment link.");

    public Task<RefundSubmissionResult> SubmitRefundAsync(
        RefundSubmissionRequest request,
        CancellationToken cancellationToken) => Task.FromResult(new RefundSubmissionResult(
        true, GatewayReference: $"SIM-REFUND-{request.RefundId}"));
}