using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>One booking's input for a single settlement line, as selected by the daily run for
/// one Tour Operator (DAILY-PAYOUT-TRANSFER spec v7 PD-2, PD-3, BR-142). <see cref="RefundAmount"/>
/// is the sum of that booking's already-<c>Processed</c> refunds (0 for Advance, which never
/// applies it — see <see cref="DailyPayoutEligibilityReader"/>).</summary>
public sealed record DailySettlementInput(
    long BookingId,
    string Kind,
    decimal PaidAmount,
    decimal RefundAmount,
    decimal CommissionRatePercent,
    decimal SettledToOperatorAmount = 0m,
    decimal OperatorReturnShare = 0m);

/// <summary>The computed amounts for one settlement line (PD-3, PD-4). <see cref="IsExcluded"/>
/// is true when <see cref="PayoutAmount"/> is zero, meaning no transfer line should be created
/// for it this run — its debt offset is still recorded against the operator.
///
/// For Advance and Final, <see cref="CommissionAmount"/> is 0 because those shares already
/// exclude TripMate's 10% commission structurally (70% + 20% + 10% = 100% of PaidAmount).
/// A Cancellation row with no earlier payout instead records the cancellation fee as gross,
/// deducts TripMate's 10% commission, and transfers the remaining 90% to the Tour Operator.
/// This satisfies the existing payment.PayoutSettlement constraint
/// (net_amount = gross_amount - commission_amount) without a schema change.
/// </summary>
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
/// Pure calculator for the daily payout run (DAILY-PAYOUT-TRANSFER spec v6 PD-3, PD-4, PD-15).
/// Takes no dependency on the database or the clock — the caller supplies already-selected,
/// already-ordered inputs for one Tour Operator and reads back exactly how much each settlement
/// is worth and how much of the operator's pending debt is still outstanding afterwards.
/// </summary>
public static class DailyPayoutAmountCalculator
{
    /// <summary>PD-3: the Advance settlement is 70% of PaidAmount, created once the booking's
    /// 24-hour Awaiting Window has ended (BR-141).</summary>
    public const decimal AdvanceRatePercent = 70m;

    /// <summary>PD-3: the Final settlement is 20% of (PaidAmount − RefundAmount), created at the
    /// first 10:00 run after the tour ends (Q6).</summary>
    public const decimal FinalRatePercent = 20m;

    /// <summary>The Tour Operator receives 90% of the cancellation fee; TripMate retains the
    /// remaining 10% as its commission.</summary>
    public const decimal CancellationOperatorRatePercent = 90m;
    public const decimal CancellationCommissionRatePercent = 10m;

    /// <summary>Rounded down to the whole VND unit (BR-146/PD-3) — VND has no subdivision, so
    /// this is a floor, not Math.Round. The Tour Operator receives the floored amount; the
    /// fractional remainder stays with TripMate (PD-3 rounding rule).</summary>
    public static decimal CalculateAdvanceAmount(decimal paidAmount) =>
        Math.Floor(paidAmount * AdvanceRatePercent / 100m);

    /// <summary>See <see cref="CalculateAdvanceAmount"/> for the rounding rule.</summary>
    public static decimal CalculateFinalAmount(decimal paidAmount, decimal refundAmount) =>
        Math.Floor((paidAmount - refundAmount) * FinalRatePercent / 100m);

    /// <summary>Informational only in v6 (see <see cref="DailySettlementCalculation"/>): the
    /// commission TripMate keeps, for future reporting integration. Not subtracted again from
    /// either settlement line. Rounded down to the whole VND unit.</summary>
    public static decimal CalculateCommission(decimal paidAmount, decimal commissionRatePercent) =>
        Math.Floor(paidAmount * commissionRatePercent / 100m);

    private static SettlementAmounts CalculateSettlementAmounts(DailySettlementInput input)
    {
        if (input.Kind == PayoutSettlementKind.Advance)
        {
            var amount = CalculateAdvanceAmount(input.PaidAmount);
            return new(amount, input.CommissionRatePercent, 0m, amount);
        }

        if (input.Kind == PayoutSettlementKind.Final)
        {
            var amount = CalculateFinalAmount(input.PaidAmount, input.RefundAmount);
            return new(amount, input.CommissionRatePercent, 0m, amount);
        }

        if (input.Kind != PayoutSettlementKind.Cancellation)
            throw new ArgumentOutOfRangeException(nameof(input.Kind), input.Kind, "Unknown settlement kind.");
        if (input.SettledToOperatorAmount < 0m || input.OperatorReturnShare < 0m)
            throw new ArgumentOutOfRangeException(nameof(input), "Settled and returned operator amounts cannot be negative.");

        decimal cancellationFee = Math.Max(input.PaidAmount - input.RefundAmount, 0m);
        decimal targetOperatorShare = Math.Floor(cancellationFee * CancellationOperatorRatePercent / 100m);
        decimal retainedByOperator = Math.Max(input.SettledToOperatorAmount - input.OperatorReturnShare, 0m);
        decimal additionalPayout = Math.Max(targetOperatorShare - retainedByOperator, 0m);

        // With no prior transfer, this row records the whole fee and its 90/10 split. Otherwise
        // it records only the payout true-up; the earlier rows already represented the commission.
        if (input.SettledToOperatorAmount == 0m)
        {
            decimal commission = cancellationFee - targetOperatorShare;
            return new(cancellationFee, CancellationCommissionRatePercent, commission, targetOperatorShare);
        }

        return new(additionalPayout, 0m, 0m, additionalPayout);
    }

    private readonly record struct SettlementAmounts(
        decimal GrossAmount, decimal CommissionRate, decimal CommissionAmount, decimal NetAmount);

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
            if (input.PaidAmount < 0)
                throw new ArgumentOutOfRangeException(nameof(inputs), "Paid amount cannot be negative.");
            if (input.RefundAmount < 0)
                throw new ArgumentOutOfRangeException(nameof(inputs), "Refund amount cannot be negative.");

            var amounts = CalculateSettlementAmounts(input);
            var offset = Math.Min(remainingDebt, amounts.NetAmount);
            remainingDebt -= offset;
            var payout = amounts.NetAmount - offset;

            results.Add(new DailySettlementCalculation(
                input.BookingId, input.Kind,
                amounts.GrossAmount,
                amounts.CommissionRate,
                amounts.CommissionAmount,
                amounts.NetAmount,
                DebtOffsetAmount: offset,
                PayoutAmount: payout,
                IsExcluded: payout <= 0m));
        }

        return new DailyPayoutBatchCalculation(results, remainingDebt);
    }
}