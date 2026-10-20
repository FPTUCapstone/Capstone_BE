namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the reporting.RevenueExports.status SQL check constraint.
/// </summary>
public enum RevenueExportStatus
{
    Queued = 1,
    Processing = 2,
    Ready = 3,
    Failed = 4,
    Expired = 5,
}