namespace TripMate.Domain.Entities;

/// <summary>
/// Shared status vocabulary for <see cref="PayoutBatch"/> and <see cref="PayoutSettlement"/>
/// (DAILY-PAYOUT-TRANSFER spec PD-10). A settlement mirrors the status of the batch that
/// carries its transfer, except "Carried Over" which a settlement can reach on its own while
/// its batch (the NEXT run's batch for the same operator) is a different, later record.
/// </summary>
public static class PayoutSettlementStatus
{
    public const string Scheduled = "Scheduled";
    public const string Processing = "Processing";
    public const string Settled = "Settled";
    public const string SettlementFailed = "SettlementFailed";
    public const string CarriedOver = "Carried Over";

    public static readonly string[] All =
    [
        Scheduled, Processing, Settled, SettlementFailed, CarriedOver,
    ];
}

public static class PayoutSettlementKind
{
    /// <summary>The settlement for a booking that was not cancelled, or not cancelled before the
    /// run started (PD-2).</summary>
    public const string Main = "Main";

    /// <summary>The settlement for the amount retained by TripMate/the Tour Operator out of a
    /// booking that was partially refunded before the run started (PD-2, BR-142). Never created
    /// for a booking refunded 100%.</summary>
    public const string Retained = "Retained";

    public static readonly string[] All = [Main, Retained];
}
