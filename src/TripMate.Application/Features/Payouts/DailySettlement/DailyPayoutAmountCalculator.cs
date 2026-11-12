using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>One booking's input for a single settlement line, as selected by the daily run for
/// one Tour Operator (DAILY-PAYOUT-TRANSFER spec v6 PD-2, PD-3). <see cref="RefundAmount"/> is
/// always 0 in this branch's scope (refund is handled by a separate branch) but is kept as an
/// explicit parameter so <see cref="DailyPayoutAmountCalculator.CalculateFinalAmount"/> matches
/// the spec formula exactly and needs no signature change once refund wiring lands.</summary>
public sealed record DailySettlementInput(
    long BookingId,
    string Kind,
    decimal PaidAmount,
    decimal RefundAmount,
    decimal CommissionRatePercent);

/// <summary>The computed amounts for one settlement line (PD-3, PD-4). <see cref="IsExcluded"/>
/// is true when <see cref="PayoutAmount"/> is zero, meaning no transfer line should be created
/// for it this run — its debt offset is still recorded against the operator.
///
/// <see cref="CommissionAmount"/> is always 0 here: the Advance (70%) and Final (20%) shares
/// already exclude TripMate's 10% commission structurally (70% + 20% + 10% = 100% of
/// PaidAmount) — v6 does not deduct commission a second time from either line. This keeps
/// <see cref="GrossAmount"/> equal to <see cref="NetAmount"/>, satisfying the existing
/// payment.PayoutSettlement constraint (net_amount = gross_amount - commission_amount) without a
/// schema change. <see cref="CommissionRate"/> is still recorded, for traceability only (the rate
/// snapshot taken at booking confirmation, BR-140) — it plays no part in this line's own math.
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

    private static decimal CalculateSettlementAmount(string kind, decimal paidAmount, decimal refundAmount) => kind switch
    {
        PayoutSettlementKind.Advance => CalculateAdvanceAmount(paidAmount),
        PayoutSettlementKind.Final => CalculateFinalAmount(paidAmount, refundAmount),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown settlement kind."),
    };

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

            var settlementAmount = CalculateSettlementAmount(input.Kind, input.PaidAmount, input.RefundAmount);
            var offset = Math.Min(remainingDebt, settlementAmount);
            remainingDebt -= offset;
            var payout = settlementAmount - offset;

            results.Add(new DailySettlementCalculation(
                input.BookingId, input.Kind,
                GrossAmount: settlementAmount,
                CommissionRate: input.CommissionRatePercent,
                CommissionAmount: 0m,
                NetAmount: settlementAmount,
                DebtOffsetAmount: offset,
                PayoutAmount: payout,
                IsExcluded: payout <= 0m));
        }

        return new DailyPayoutBatchCalculation(results, remainingDebt);
    }
}