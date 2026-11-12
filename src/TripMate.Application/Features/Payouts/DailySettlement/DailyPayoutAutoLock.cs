namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>
/// Constants for the automatic lock of a Tour Operator with unrecovered pending debt
/// (DAILY-PAYOUT-TRANSFER spec v6, BR-189, PD-9A).
/// </summary>
public static class DailyPayoutAutoLock
{
    /// <summary>BR-189: a debt unrecovered for this long triggers the automatic lock.</summary>
    public static readonly TimeSpan UnrecoveredDebtGracePeriod = TimeSpan.FromDays(15);

    /// <summary>The lock reason recorded on the user (PD-9A) — not a free-text message, so it is
    /// a constant rather than a magic string scattered across callers.</summary>
    public const string UnrecoveredDebtLockReason = "Unrecovered pending debt (BR-189)";
}