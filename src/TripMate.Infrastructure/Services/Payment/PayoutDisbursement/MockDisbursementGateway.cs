using TripMate.Application.Features.Admin.PayoutSettlements.Processing;

namespace TripMate.Infrastructure.Services.Payment.PayoutDisbursement;

/// <summary>
/// Default payout gateway for Development, Test and CI: it accepts every transfer and reports it
/// as succeeded without any network call, so the whole UC-65 flow can run without moving money.
/// </summary>
public sealed class MockDisbursementGateway : IPayoutDisbursementGateway
{
    private const string ReferencePrefix = "mock-payout-";

    public Task<PayoutSubmitResult> SubmitAsync(PayoutSubmitRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(PayoutSubmitResult.Accepted(ReferencePrefix + request.ReferenceId));

    public Task<PayoutStatusResult> GetStatusAsync(string gatewayPayoutId, CancellationToken cancellationToken) =>
        Task.FromResult(PayoutStatusResult.Of(PayoutGatewayState.Succeeded));

    // The mock keeps no state, so it never claims to have seen a previous submission.
    public Task<PayoutLookupResult> FindByReferenceAsync(string referenceId, CancellationToken cancellationToken) =>
        Task.FromResult(PayoutLookupResult.NotFound());
}
