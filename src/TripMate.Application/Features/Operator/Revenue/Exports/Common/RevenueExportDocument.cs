using TripMate.Application.Features.Operator.Revenue.GetReport;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Revenue.Exports.Common;

/// <summary>The filter snapshot a renderer prints on the file header; never the caller's live UI filters (BR-110).</summary>
public sealed record RevenueExportFilterSnapshot(
    DateOnly StartDate,
    DateOnly EndDate,
    string Granularity,
    long? TourId,
    string? TourName);

/// <summary>
/// Formatter-neutral report document: the same data an <see cref="IRevenueExportRenderer"/>
/// turns into bytes, regardless of file format. TourDetails is empty for <see cref="RevenueExportScope.Summary"/>.
/// </summary>
public sealed record RevenueExportDocument(
    RevenueExportFilterSnapshot Filters,
    OperatorRevenueSummaryDto Summary,
    IReadOnlyList<OperatorTourRevenueDto> TourDetails,
    RevenueExportScope Scope,
    DateTimeOffset GeneratedAtUtc);