using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// UC-45: a durable record of one Tour Operator revenue export request. The filter snapshot
/// (StartDate/EndDate/Granularity/TourId/Format/Scope) is immutable once created (BR-110):
/// retrying or downloading an export never reads the operator's current UI filters.
/// Maps reporting.RevenueExports.
/// </summary>
public class RevenueExport : BaseEntity
{
    private RevenueExport()
    {
    }

    public long OperatorUserId { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly EndDate { get; private set; }

    public string Granularity { get; private set; } = string.Empty;

    public long? TourId { get; private set; }

    public RevenueExportFormat Format { get; private set; }

    public RevenueExportScope Scope { get; private set; }

    public RevenueExportStatus Status { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public int? RowCount { get; private set; }

    public string? FileName { get; private set; }

    /// <summary>Opaque private-storage reference; never a bearer download URL (see spec §Persistence).</summary>
    public string? StorageKey { get; private set; }

    public string? FailureCode { get; private set; }

    /// <summary>Optimistic concurrency token guarding the atomic worker claim (one worker per record).</summary>
    public byte[] RowVersion { get; private set; } = [];

    public static RevenueExport Create(
        long operatorUserId,
        DateOnly startDate,
        DateOnly endDate,
        string granularity,
        long? tourId,
        RevenueExportFormat format,
        RevenueExportScope scope,
        DateTimeOffset requestedAtUtc)
    {
        if (operatorUserId <= 0) throw new ArgumentOutOfRangeException(nameof(operatorUserId));
        if (endDate < startDate) throw new ArgumentException("End date must be on or after start date.", nameof(endDate));
        ArgumentException.ThrowIfNullOrWhiteSpace(granularity);
        if (tourId is <= 0) throw new ArgumentOutOfRangeException(nameof(tourId));

        return new RevenueExport
        {
            OperatorUserId = operatorUserId,
            StartDate = startDate,
            EndDate = endDate,
            Granularity = granularity,
            TourId = tourId,
            Format = format,
            Scope = scope,
            Status = RevenueExportStatus.Queued,
            RequestedAtUtc = requestedAtUtc.ToUniversalTime(),
        };
    }

    /// <summary>Atomically claims a Queued record for background processing. False if already claimed.</summary>
    public bool ClaimForProcessing(DateTimeOffset nowUtc)
    {
        if (Status != RevenueExportStatus.Queued) return false;
        Status = RevenueExportStatus.Processing;
        return true;
    }

    /// <summary>Transitions Queued (synchronous path) or Processing (worker path) to Ready.</summary>
    public bool MarkReady(
        string fileName,
        string storageKey,
        int rowCount,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset nowUtc)
    {
        if (Status is not (RevenueExportStatus.Queued or RevenueExportStatus.Processing)) return false;
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        Status = RevenueExportStatus.Ready;
        FileName = fileName;
        StorageKey = storageKey;
        RowCount = rowCount;
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        CompletedAtUtc = nowUtc.ToUniversalTime();
        return true;
    }

    public bool MarkFailed(string failureCode, DateTimeOffset nowUtc)
    {
        if (Status is not (RevenueExportStatus.Queued or RevenueExportStatus.Processing)) return false;
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);

        Status = RevenueExportStatus.Failed;
        FailureCode = failureCode;
        CompletedAtUtc = nowUtc.ToUniversalTime();
        return true;
    }

    public bool MarkExpired(DateTimeOffset nowUtc)
    {
        if (Status != RevenueExportStatus.Ready) return false;
        Status = RevenueExportStatus.Expired;
        CompletedAtUtc = nowUtc.ToUniversalTime();
        return true;
    }

    public bool IsDownloadable(DateTimeOffset nowUtc) =>
        Status == RevenueExportStatus.Ready &&
        (!ExpiresAtUtc.HasValue || ExpiresAtUtc.Value > nowUtc);
}
