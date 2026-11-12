namespace TripMate.Domain.Entities;

/// <summary>
/// Shared status vocabulary for <see cref="PayoutBatch"/> and <see cref="PayoutSettlement"/>
/// (DAILY-PAYOUT-TRANSFER spec v6 PD-10). A settlement mirrors the status of the batch that
/// carries its transfer. "Carried Over" is removed in v6 (not in SRS rev10-5); "Resolved" is
/// added for UC-73's [Mark as Resolved] administrator action on a permanently failed batch.
/// </summary>
public static class PayoutSettlementStatus
{
    public const string Scheduled = "Scheduled";
    public const string Processing = "Processing";
    public const string Settled = "Settled";
    public const string SettlementFailed = "SettlementFailed";
    public const string Resolved = "Resolved";

    public static readonly string[] All =
    [
        Scheduled, Processing, Settled, SettlementFailed, Resolved,
    ];
}

public static class PayoutSettlementKind
{
    /// <summary>70% of PaidAmount, created once the booking's 24-hour Awaiting Window has ended
    /// (BR-141, PD-1A, PD-2, PD-3).</summary>
    public const string Advance = "Advance";

    /// <summary>20% of (PaidAmount − RefundAmount), created at the first 10:00 (Vietnam time) run
    /// after the tour ends (BR-141, PD-2, PD-3, Q6).</summary>
    public const string Final = "Final";

    public static readonly string[] All = [Advance, Final];
}