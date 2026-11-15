using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;

namespace TripMate.Application.Features.Operator.Revenue.Exports.GetHistory;

public sealed record GetRevenueExportHistoryQuery(int Page = 1, int PageSize = 20)
    : IRequest<Result<RevenueExportHistoryResponse>>;

public sealed record RevenueExportHistoryResponse(
    IReadOnlyList<RevenueExportResponseDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);