using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Revenue.Exports.GetHistory;

public sealed class GetRevenueExportHistoryQueryHandler(
    IApplicationDbContext db, ICurrentUserService currentUser, IDateTimeProvider clock, IRevenueExportStorage storage)
    : IRequestHandler<GetRevenueExportHistoryQuery, Result<RevenueExportHistoryResponse>>
{
    private static readonly TimeSpan DownloadUrlValidity = TimeSpan.FromMinutes(15);

    public async Task<Result<RevenueExportHistoryResponse>> Handle(
        GetRevenueExportHistoryQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<RevenueExportHistoryResponse>(
                RevenueExportErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");
        }

        int page = Math.Max(1, request.Page), pageSize = Math.Clamp(request.PageSize, 1, 100);
        var now = clock.UtcNow;

        var query = db.RevenueExports.AsNoTracking()
            .Where(x => x.OperatorUserId == operatorId)
            .OrderByDescending(x => x.RequestedAtUtc);

        var totalCount = await query.CountAsync(ct);
        var pageRows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var items = pageRows.Select(x => ToDto(x, now, storage)).ToList();

        return Result.Success(new RevenueExportHistoryResponse(
            items, totalCount, page, pageSize,
            totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize)));
    }

    private static RevenueExportResponseDto ToDto(RevenueExport export, DateTimeOffset now, IRevenueExportStorage storage)
    {
        Uri? downloadUrl = null;
        if (export.IsDownloadable(now) && export.StorageKey is not null)
        {
            var linkExpiry = export.ExpiresAtUtc!.Value < now.Add(DownloadUrlValidity)
                ? export.ExpiresAtUtc.Value
                : now.Add(DownloadUrlValidity);
            downloadUrl = storage.CreateTemporaryDownloadUrl(export.StorageKey, linkExpiry);
        }

        return new RevenueExportResponseDto(
            export.Id, export.StartDate, export.EndDate, export.Granularity, export.TourId,
            export.Format.ToString().ToLowerInvariant(),
            export.Scope == RevenueExportScope.SummaryAndDetails ? "summary_and_details" : "summary",
            export.Status.ToString(), export.RequestedAtUtc, export.CompletedAtUtc, export.ExpiresAtUtc,
            export.FileName, downloadUrl?.AbsoluteUri);
    }
}