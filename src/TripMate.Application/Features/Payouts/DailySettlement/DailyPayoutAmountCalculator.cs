namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>One booking's (or retained part's) raw amount, as selected by the daily run for a
/// single Tour Operator (DAILY-PAYOUT-TRANSFER spec PD-2, PD-3).</summary>
public sealed record DailySettlementInput(
    long BookingId,
    string Kind,
    decimal GrossAmount,
    decimal CommissionRatePercent);

/// <summary>The computed amounts for one settlement (PD-3, PD-4). <see cref="IsExcluded"/> is
/// true when <see cref="PayoutAmount"/> is zero, meaning no settlement/transfer line should be
/// created for it this run (PD-3) — its debt offset is still recorded against the operator.</summary>
public sealed record DailySettlementCalculation(
    long BookingId,
    string Kind,
    decimal GrossAmount,
    decimal CommissionRate,
    decimal CommissionAmount,
    decimal NetAmount,
    decimal DebtOffsetAmount,
    decimal PayoutAmount,
    bool IsExcluded);

/// <summary>The computed settlements for every booking of one Tour Operator in one run, plus any
/// outstanding debt left over for the next run (PD-15).</summary>
public sealed record DailyPayoutBatchCalculation(
    IReadOnlyList<DailySettlementCalculation> Settlements,
    decimal RemainingDebt);

/// <summary>
/// Pure calculator for the daily payout run (DAILY-PAYOUT-TRANSFER spec PD-3, PD-4, PD-15).
/// Takes no dependency on the database or the clock — the caller supplies already-selected,
/// already-ordered inputs for one Tour Operator and reads back exactly how much each settlement
/// is worth and how much of the operator's pending debt is still outstanding afterwards.
/// </summary>
public static class DailyPayoutAmountCalculator
{
    /// <summary>Commission is rounded down to the whole VND unit (BR-146/PD-3) — VND has no
    /// subdivision, so this is a floor, not Math.Round.</summary>
    public static decimal CalculateCommission(decimal grossAmount, decimal commissionRatePercent) =>
        Math.Floor(grossAmount * commissionRatePercent / 100m);

    /// <summary>
    /// Computes every settlement for one operator's inputs in this run, applying the operator's
    /// outstanding debt (PD-15) in the order the inputs are given — the caller is responsible for
    /// passing them in settlement-creation order (oldest booking first).
    /// </summary>
    public static DailyPayoutBatchCalculation Calculate(
        IReadOnlyList<DailySettlementInput> inputs, decimal outstandingDebt)
    {
        if (outstandingDebt < 0)
            throw new ArgumentOutOfRangeException(nameof(outstandingDebt), "Outstanding debt cannot be negative.");

        var remainingDebt = outstandingDebt;
        var results = new List<DailySettlementCalculation>(inputs.Count);

        foreach (var input in inputs)
        {
            if (input.GrossAmount < 0)
                throw new ArgumentOutOfRangeException(nameof(input), "Gross amount cannot be negative.");

            var commission = CalculateCommission(input.GrossAmount, input.CommissionRatePercent);
            var net = input.GrossAmount - commission;
            var offset = Math.Min(remainingDebt, net);
            remainingDebt -= offset;
            var payout = net - offset;

            results.Add(new DailySettlementCalculation(
                input.BookingId, input.Kind, input.GrossAmount, input.CommissionRatePercent,
                commission, net, offset, payout, IsExcluded: payout <= 0m));
        }

        return new DailyPayoutBatchCalculation(results, remainingDebt);
    }
}