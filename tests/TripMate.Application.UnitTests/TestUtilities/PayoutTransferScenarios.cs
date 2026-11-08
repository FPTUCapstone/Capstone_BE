using TripMate.Application.Features.Payouts.DailySettlement;

namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>Always returns the same outcome, for every gateway reference (DAILY-PAYOUT-TRANSFER tests).</summary>
internal sealed class FixedOutcomeScenario(PayoutTransferOutcome outcome, string? failureReason = null) : IPayoutTransferScenario
{
    public PayoutTransferResult Send(string gatewayReference, long tourOperatorUserId, decimal amount) =>
        new(outcome, gatewayReference, outcome == PayoutTransferOutcome.Succeeded ? null : failureReason ?? outcome.ToString());

    public PayoutTransferResult Lookup(string gatewayReference) =>
        new(outcome, gatewayReference, outcome == PayoutTransferOutcome.Succeeded ? null : failureReason ?? outcome.ToString());
}

/// <summary>Always reports <paramref name="lookupOutcome"/> for a lookup and
/// <paramref name="sendOutcome"/> for a send — for a sweep test that resends after an unresolved
/// lookup (DAILY-PAYOUT-TRANSFER PD-7a).</summary>
internal sealed class LookupThenSendScenario(PayoutTransferOutcome lookupOutcome, PayoutTransferOutcome sendOutcome) : IPayoutTransferScenario
{
    public PayoutTransferResult Send(string gatewayReference, long tourOperatorUserId, decimal amount) =>
        new(sendOutcome, gatewayReference, sendOutcome == PayoutTransferOutcome.Succeeded ? null : sendOutcome.ToString());

    public PayoutTransferResult Lookup(string gatewayReference) =>
        new(lookupOutcome, gatewayReference, lookupOutcome == PayoutTransferOutcome.Succeeded ? null : lookupOutcome.ToString());
}

/// <summary>Returns each outcome in order on successive Send calls, then repeats the last one.
/// Lookup always reports the most recently sent outcome (DAILY-PAYOUT-TRANSFER PD-7a tests).</summary>
internal sealed class SequenceOutcomeScenario(params PayoutTransferOutcome[] outcomes) : IPayoutTransferScenario
{
    private int _index;
    private PayoutTransferOutcome _last = outcomes.Length > 0 ? outcomes[0] : PayoutTransferOutcome.Succeeded;

    public PayoutTransferResult Send(string gatewayReference, long tourOperatorUserId, decimal amount)
    {
        var outcome = outcomes[Math.Min(_index, outcomes.Length - 1)];
        _index++;
        _last = outcome;
        return new PayoutTransferResult(outcome, gatewayReference, outcome == PayoutTransferOutcome.Succeeded ? null : outcome.ToString());
    }

    public PayoutTransferResult Lookup(string gatewayReference) =>
        new(_last, gatewayReference, _last == PayoutTransferOutcome.Succeeded ? null : _last.ToString());
}
