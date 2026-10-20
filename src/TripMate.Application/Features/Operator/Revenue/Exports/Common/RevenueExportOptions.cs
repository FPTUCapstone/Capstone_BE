namespace TripMate.Application.Features.Operator.Revenue.Exports.Common;

/// <summary>UC-45 export generation thresholds, retention, and worker policy (configuration, not client input).</summary>
public sealed class RevenueExportOptions
{
    public const string SectionName = "RevenueExport";

    /// <summary>Detail-row count at or below which an export is generated synchronously (201 Ready).</summary>
    public int SynchronousRowThreshold { get; set; } = 500;

    /// <summary>How long a Ready export's private asset and download link remain available.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Maximum Queued records claimed per worker sweep.</summary>
    public int WorkerBatchSize { get; set; } = 5;

    /// <summary>Delay between worker sweeps when no work was claimed.</summary>
    public int WorkerPollIntervalSeconds { get; set; } = 10;

    /// <summary>Delay between the expiry-cleanup sweeps that remove assets past ExpiresAtUtc.</summary>
    public int ExpiryCleanupIntervalMinutes { get; set; } = 60;
}
