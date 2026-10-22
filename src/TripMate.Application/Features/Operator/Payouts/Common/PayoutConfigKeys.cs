namespace TripMate.Application.Features.Operator.Payouts.Common;

/// <summary>UC-46 dbo.SystemConfigs keys (seeded by migration 20261022_uc46_payout_settlement_request.sql).</summary>
public static class PayoutConfigKeys
{
    public const string MinimumAmountVnd = "payout.minimum_amount_vnd";
    public const string RefundHoldDays = "payout.refund_hold_days";

    /// <summary>Production fallback if the config row is somehow missing; the seed migration
    /// normally guarantees the row exists. Never silently used to mask a missing seed in
    /// production — callers should prefer failing loudly in that case instead.
    /// Confirmed 2026-10-23 (supersedes the earlier unconfirmed 5,000,000 placeholder).</summary>
    public const decimal DefaultMinimumAmountVnd = 1_000_000m;

    public const int DefaultRefundHoldDays = 3;
}
