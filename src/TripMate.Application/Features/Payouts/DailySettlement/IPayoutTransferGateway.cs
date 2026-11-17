namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>Result of sending or looking up a transfer (DAILY-PAYOUT-TRANSFER spec, chế độ giả
/// lập / PD-6, PD-7a). <see cref="Timeout"/> means the outcome is unknown — PD-7a requires a
/// lookup by reference before any retry, specifically to distinguish this from a real failure.</summary>
public enum PayoutTransferOutcome
{
    Succeeded,
    Failed,
    Timeout,
    Rejected,
}

public sealed record PayoutTransferResult(
    PayoutTransferOutcome Outcome,
    string GatewayReference,
    string? FailureReason = null);

/// <summary>
/// The boundary TripMate's payout run talks to in order to move money to a Tour Operator. The
/// real implementation (VNPay/PayOS/banking) does not exist yet; the production binding is
/// <see cref="SimulatedPayoutTransferGateway"/> (spec "Chế độ giả lập"). Swapping in a real
/// implementation later changes nothing about the run's own logic.
/// </summary>
public interface IPayoutTransferGateway
{
    /// <summary>Sends a transfer order for one <c>PayoutBatch</c> (PD-14). Called once per attempt
    /// (PD-7) — the caller does the retry scheduling, not this method.</summary>
    Task<PayoutTransferResult> SendAsync(string gatewayReference, long tourOperatorUserId, decimal amount, CancellationToken cancellationToken);

    /// <summary>Looks up the final outcome of a previously sent transfer by its reference. Called
    /// before every retry (PD-7a) and during post-run reconciliation (PD-17), so a transfer that
    /// actually went through is never sent a second time.</summary>
    Task<PayoutTransferResult> LookupAsync(string gatewayReference, CancellationToken cancellationToken);
}

/// <summary>The configurable behaviour behind <see cref="SimulatedPayoutTransferGateway"/>. The
/// production registration uses <see cref="AlwaysSucceedPayoutTransferScenario"/>.</summary>
public interface IPayoutTransferScenario
{
    PayoutTransferResult Send(string gatewayReference, long tourOperatorUserId, decimal amount);

    PayoutTransferResult Lookup(string gatewayReference);
}

/// <summary>Default scenario: every transfer succeeds immediately. Used outside tests, since no
/// real bank connection exists yet.</summary>
public sealed class AlwaysSucceedPayoutTransferScenario : IPayoutTransferScenario
{
    public PayoutTransferResult Send(string gatewayReference, long tourOperatorUserId, decimal amount) =>
        new(PayoutTransferOutcome.Succeeded, gatewayReference);

    public PayoutTransferResult Lookup(string gatewayReference) =>
        new(PayoutTransferOutcome.Succeeded, gatewayReference);
}

/// <summary>
/// Simulated adapter for <see cref="IPayoutTransferGateway"/> (spec "Chế độ giả lập"). Every call
/// goes through exactly the same run/retry/reconciliation logic a real gateway would; only the
/// outcome is manufactured by <paramref name="scenario"/> instead of a real bank response.
/// </summary>
public sealed class SimulatedPayoutTransferGateway(IPayoutTransferScenario scenario) : IPayoutTransferGateway
{
    public Task<PayoutTransferResult> SendAsync(string gatewayReference, long tourOperatorUserId, decimal amount, CancellationToken cancellationToken) =>
        Task.FromResult(scenario.Send(gatewayReference, tourOperatorUserId, amount));

    public Task<PayoutTransferResult> LookupAsync(string gatewayReference, CancellationToken cancellationToken) =>
        Task.FromResult(scenario.Lookup(gatewayReference));
}

/// <summary>Builds the stable simulated gateway reference for one Tour Operator's batch in one
/// run (spec: <c>SIM-{RunDate}-{TourOperatorId}-{BatchId}</c>).</summary>
public static class PayoutGatewayReference
{
    public static string Create(DateOnly runDate, long tourOperatorUserId, long batchId) =>
        $"SIM-{runDate:yyyyMMdd}-{tourOperatorUserId}-{batchId}";
}