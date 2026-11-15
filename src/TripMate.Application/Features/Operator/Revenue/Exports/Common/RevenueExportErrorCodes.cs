namespace TripMate.Application.Features.Operator.Revenue.Exports.Common;

/// <summary>
/// The canonical SRS §3.8.5.2 error codes for UC-45. Unlike most features in this codebase,
/// these constants are literally the message codes the spec names (MSG01/MSG126/MSG128/MSG117),
/// not an internal descriptive slug, because the spec's acceptance criteria name them directly.
/// </summary>
public static class RevenueExportErrorCodes
{
    /// <summary>Missing/invalid format, scope, or granularity, or an invalid date range.</summary>
    public const string ValidationFailed = "MSG01";

    /// <summary>Non-approved operator or a foreign tour filter (BR-105): never discloses which.</summary>
    public const string AccessRequired = "MSG126";

    /// <summary>No reportable records match the filter snapshot.</summary>
    public const string NoRecords = "MSG128";

    /// <summary>Render or durable-storage failure; no partial file is ever offered.</summary>
    public const string GenerationFailed = "MSG117";

    /// <summary>A missing, foreign, or otherwise unavailable export id; indistinguishable from each other.</summary>
    public const string NotFound = "revenue_export.not_found";
}