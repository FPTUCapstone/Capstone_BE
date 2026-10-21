namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the reporting.RevenueExports.format SQL check constraint.
/// </summary>
public enum RevenueExportFormat
{
    Xlsx = 1,
    Csv = 2,
    Pdf = 3,
}