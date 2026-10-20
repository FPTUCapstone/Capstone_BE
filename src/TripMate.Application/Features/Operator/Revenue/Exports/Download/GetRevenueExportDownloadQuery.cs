using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Revenue.Exports.Download;

public sealed record GetRevenueExportDownloadQuery(long ExportId)
    : IRequest<Result<RevenueExportDownloadResponse>>;

public sealed record RevenueExportDownloadResponse(string FileName, Uri DownloadUrl);
