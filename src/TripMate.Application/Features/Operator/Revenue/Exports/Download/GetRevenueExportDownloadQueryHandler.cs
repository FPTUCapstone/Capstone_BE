using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Revenue.Exports.Download;

public sealed class GetRevenueExportDownloadQueryHandler(
    IApplicationDbContext db, ICurrentUserService currentUser, IDateTimeProvider clock, IRevenueExportStorage storage)
    : IRequestHandler<GetRevenueExportDownloadQuery, Result<RevenueExportDownloadResponse>>
{
    private static readonly TimeSpan DownloadUrlValidity = TimeSpan.FromMinutes(15);

    public async Task<Result<RevenueExportDownloadResponse>> Handle(
        GetRevenueExportDownloadQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<RevenueExportDownloadResponse>(
                RevenueExportErrorCodes.NotFound, "The requested export was not found.");
        }

        var now = clock.UtcNow;

        // BR-105: scoping by caller in the WHERE clause makes a foreign export id
        // indistinguishable from one that does not exist at all.
        var export = await db.RevenueExports.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.ExportId && x.OperatorUserId == operatorId, ct);

        if (export is null || !export.IsDownloadable(now) || export.StorageKey is null || export.FileName is null)
        {
            return Result.Failure<RevenueExportDownloadResponse>(
                RevenueExportErrorCodes.NotFound, "The requested export was not found.");
        }

        var linkExpiry = export.ExpiresAtUtc!.Value < now.Add(DownloadUrlValidity)
            ? export.ExpiresAtUtc.Value
            : now.Add(DownloadUrlValidity);
        var downloadUrl = storage.CreateTemporaryDownloadUrl(export.StorageKey, linkExpiry);

        if (downloadUrl is null)
        {
            return Result.Failure<RevenueExportDownloadResponse>(
                RevenueExportErrorCodes.NotFound, "The requested export was not found.");
        }

        return Result.Success(new RevenueExportDownloadResponse(export.FileName, downloadUrl));
    }
}