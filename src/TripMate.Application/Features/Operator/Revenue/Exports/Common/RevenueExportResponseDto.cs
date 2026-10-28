namespace TripMate.Application.Features.Operator.Revenue.Exports.Common;

/// <summary>
/// Shared response shape for create, history, and download. DownloadUrl is populated only
/// while Status is Ready and ExpiresAtUtc has not passed (never a persisted/bearer URL).
/// </summary>
public sealed record RevenueExportResponseDto(
    long Id,
    DateOnly StartDate,
    DateOnly EndDate,
    string Granularity,
    long? TourId,
    string Format,
    string Scope,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? FileName,
    string? DownloadUrl);