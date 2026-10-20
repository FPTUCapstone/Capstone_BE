namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the reporting.RevenueExports.scope SQL check constraint.
/// </summary>
public enum RevenueExportScope
{
    Summary = 1,
    SummaryAndDetails = 2,
}
