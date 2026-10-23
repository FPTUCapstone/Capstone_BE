using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Revenue.Exports.Create;

public sealed class CreateRevenueExportCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    OperatorRevenueAggregator aggregator,
    IRevenueExportRenderer renderer,
    IRevenueExportStorage storage,
    RevenueExportOptions options)
    : IRequestHandler<CreateRevenueExportCommand, Result<RevenueExportResponseDto>>
{
    public async Task<Result<RevenueExportResponseDto>> Handle(
        CreateRevenueExportCommand request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId ||
            currentUser.Role != nameof(UserRole.TourOperator) ||
            request.EndDate < request.StartDate)
        {
            return Result.Failure<RevenueExportResponseDto>(
                RevenueExportErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");
        }

        if (!OperatorRevenueAggregator.IsValidGranularity(request.Granularity))
        {
            return Result.Failure<RevenueExportResponseDto>(
                RevenueExportErrorCodes.ValidationFailed, "Granularity must be 'daily', 'weekly', or 'monthly'.");
        }

        if (!string.Equals(request.Format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<RevenueExportResponseDto>(
                RevenueExportErrorCodes.ValidationFailed,
                "Only the Excel (.xlsx) export format is currently supported.");
        }

        if (!TryParseScope(request.Scope, out var scope))
        {
            return Result.Failure<RevenueExportResponseDto>(
                RevenueExportErrorCodes.ValidationFailed, "Scope must be 'summary' or 'summary_and_details'.");
        }

        var active = await db.OperatorProfiles.AnyAsync(
            p => p.UserId == operatorId && p.ApprovalStatus == OperatorApprovalStatus.Approved
                && p.User.Status == AccountStatus.Active, ct);
        if (!active)
        {
            return Result.Failure<RevenueExportResponseDto>(
                RevenueExportErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");
        }

        string? tourName = null;
        if (request.TourId is long tourId)
        {
            // BR-105: a foreign tour id is indistinguishable from an unavailable one.
            tourName = await db.Tours
                .Where(t => t.Id == tourId && t.OperatorUserId == operatorId)
                .Select(t => t.Title)
                .FirstOrDefaultAsync(ct);
            if (tourName is null)
            {
                return Result.Failure<RevenueExportResponseDto>(
                    RevenueExportErrorCodes.AccessRequired, "The requested tour is not available.");
            }
        }

        var now = clock.UtcNow;
        var aggregate = await aggregator.AggregateAsync(
            operatorId, request.StartDate, request.EndDate, request.Granularity, request.TourId, ct);
        if (aggregate.Summary.TotalBookings == 0 && aggregate.Summary.RefundedAmount == 0)
        {
            return Result.Failure<RevenueExportResponseDto>(
                RevenueExportErrorCodes.NoRecords, "No revenue records found for the selected period.");
        }

        var export = RevenueExport.Create(
            operatorId, request.StartDate, request.EndDate, request.Granularity, request.TourId,
            RevenueExportFormat.Xlsx, scope, now);

        var isSynchronous = aggregate.AllTourDetails.Count <= options.SynchronousRowThreshold;

        await db.ExecuteInSerializableTransactionAsync<bool>(async token =>
        {
            db.RevenueExports.Add(export);
            await db.SaveChangesAsync(token);

            var auditPayload = JsonSerializer.Serialize(new
            {
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                Granularity = request.Granularity,
                TourId = request.TourId,
                Format = export.Format.ToString(),
                Scope = export.Scope.ToString(),
                Mode = isSynchronous ? "synchronous" : "asynchronous",
            });
            db.AuditLogs.Add(AuditLog.CreateRevenueExportRequested(operatorId, export.Id, auditPayload, now));
            await db.SaveChangesAsync(token);
            return true;
        }, ct);

        if (!isSynchronous)
        {
            // Left Queued for the background worker (Task 5) to claim and render.
            return Result.Success(ToDto(export, null));
        }

        var document = new RevenueExportDocument(
            new RevenueExportFilterSnapshot(request.StartDate, request.EndDate, request.Granularity, request.TourId, tourName),
            aggregate.Summary, aggregate.AllTourDetails, scope, now);

        byte[] bytes;
        try
        {
            bytes = renderer.Render(document);
        }
        catch (Exception)
        {
            export.MarkFailed("render_error", clock.UtcNow);
            await db.SaveChangesAsync(ct);
            return Result.Failure<RevenueExportResponseDto>(
                RevenueExportErrorCodes.GenerationFailed, "Failed to generate export file. Please retry.");
        }

        var fileName = BuildFileName(export.Id, request.StartDate, request.EndDate, renderer.FileExtension);
        var publicId = storage.AllocatePublicId(operatorId, export.Id);
        var uploadResult = await storage.UploadAsync(
            new RevenueExportStorageUpload(publicId, renderer.ContentType, bytes), ct);

        if (!uploadResult.IsSuccess)
        {
            export.MarkFailed(uploadResult.SafeErrorCode ?? "storage_unavailable", clock.UtcNow);
            await db.SaveChangesAsync(ct);
            return Result.Failure<RevenueExportResponseDto>(
                RevenueExportErrorCodes.GenerationFailed, "Failed to generate export file. Please retry.");
        }

        var expiresAt = clock.UtcNow.AddDays(options.RetentionDays);
        export.MarkReady(fileName, uploadResult.StorageKey!, aggregate.AllTourDetails.Count, expiresAt, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        var downloadUrl = storage.CreateTemporaryDownloadUrl(uploadResult.StorageKey!, expiresAt);
        return Result.Success(ToDto(export, downloadUrl));
    }

    private static bool TryParseScope(string? scope, out RevenueExportScope parsed)
    {
        switch (scope?.Trim().ToLowerInvariant())
        {
            case "summary":
                parsed = RevenueExportScope.Summary;
                return true;
            case "summary_and_details":
                parsed = RevenueExportScope.SummaryAndDetails;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static string BuildFileName(long exportId, DateOnly startDate, DateOnly endDate, string extension) =>
        $"tripmate-revenue-{startDate:yyyyMMdd}-{endDate:yyyyMMdd}-{exportId}.{extension}";

    private static RevenueExportResponseDto ToDto(RevenueExport export, Uri? downloadUrl) =>
        new(
            export.Id, export.StartDate, export.EndDate, export.Granularity, export.TourId,
            export.Format.ToString().ToLowerInvariant(), ToScopeString(export.Scope), export.Status.ToString(),
            export.RequestedAtUtc, export.CompletedAtUtc, export.ExpiresAtUtc, export.FileName,
            downloadUrl?.AbsoluteUri);

    private static string ToScopeString(RevenueExportScope scope) =>
        scope == RevenueExportScope.SummaryAndDetails ? "summary_and_details" : "summary";
}