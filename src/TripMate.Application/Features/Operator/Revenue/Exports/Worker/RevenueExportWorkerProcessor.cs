using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Revenue.Exports.Worker;

/// <summary>
/// UC-45's durable asynchronous export path: claims Queued records one batch at a time and
/// regenerates them from their own immutable filter snapshot (BR-110), never from live input.
/// Restart-safe: every sweep first reclaims any record left in Processing past the stale
/// timeout — the worker that claimed it crashed, was redeployed, or was cancelled before
/// reaching MarkReady/MarkFailed — so a record is requeued (or, past the retry limit, marked
/// Failed) instead of being silently stuck forever.
/// </summary>
public sealed class RevenueExportWorkerProcessor(
    IApplicationDbContext db,
    IDateTimeProvider clock,
    OperatorRevenueAggregator aggregator,
    IRevenueExportRenderer renderer,
    IRevenueExportStorage storage,
    RevenueExportOptions options)
{
    public async Task<int> ProcessQueuedBatchAsync(CancellationToken cancellationToken)
    {
        await ReclaimStaleProcessingAsync(cancellationToken);

        var batch = await db.RevenueExports
            .Where(x => x.Status == RevenueExportStatus.Queued)
            .OrderBy(x => x.RequestedAtUtc)
            .Take(options.WorkerBatchSize)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var export in batch)
        {
            if (await TryClaimAsync(export, cancellationToken))
            {
                await RenderAndCompleteAsync(export, cancellationToken);
                processed++;
            }
        }

        return processed;
    }

    /// <summary>
    /// Recovers records a worker claimed (Processing) but never resolved — crashed, redeployed,
    /// or cancelled before MarkReady/MarkFailed. Without this sweep such a record stays
    /// Processing forever and the FE polls it indefinitely (see RevenueExport.ReclaimIfStale).
    /// </summary>
    private async Task ReclaimStaleProcessingAsync(CancellationToken cancellationToken)
    {
        var staleTimeout = TimeSpan.FromMinutes(options.StaleProcessingTimeoutMinutes);
        var staleBefore = clock.UtcNow - staleTimeout;

        // A null ProcessingStartedAtUtc means the record was claimed by a worker from before this
        // recovery existed — it must be picked up here too, not excluded forever by this filter.
        var staleBatch = await db.RevenueExports
            .Where(x => x.Status == RevenueExportStatus.Processing
                && (x.ProcessingStartedAtUtc == null || x.ProcessingStartedAtUtc <= staleBefore))
            .Take(options.WorkerBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var export in staleBatch)
        {
            if (!export.ReclaimIfStale(clock.UtcNow, staleTimeout, options.MaxProcessingAttempts))
            {
                continue;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker instance already resolved this record; not an error for this sweep.
                db.ClearTrackedEntities();
            }
        }
    }

    /// <summary>
    /// Claims via the entity's in-memory guard, then lets the row_version concurrency token
    /// reject a second worker that loaded the same Queued row before this save committed.
    /// </summary>
    private async Task<bool> TryClaimAsync(RevenueExport export, CancellationToken cancellationToken)
    {
        if (!export.ClaimForProcessing(clock.UtcNow))
        {
            return false;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker claimed this record first; not an error for this sweep.
            db.ClearTrackedEntities();
            return false;
        }
    }

    private async Task RenderAndCompleteAsync(RevenueExport export, CancellationToken cancellationToken)
    {
        try
        {
            string? tourName = export.TourId.HasValue
                ? await db.Tours
                    .Where(t => t.Id == export.TourId.Value)
                    .Select(t => t.Title)
                    .FirstOrDefaultAsync(cancellationToken)
                : null;

            var aggregate = await aggregator.AggregateAsync(
                export.OperatorUserId, export.StartDate, export.EndDate, export.Granularity, export.TourId,
                cancellationToken);

            var document = new RevenueExportDocument(
                new RevenueExportFilterSnapshot(
                    export.StartDate, export.EndDate, export.Granularity, export.TourId, tourName),
                aggregate.Summary, aggregate.AllTourDetails, export.Scope, clock.UtcNow);

            var bytes = renderer.Render(document);
            var fileName =
                $"tripmate-revenue-{export.StartDate:yyyyMMdd}-{export.EndDate:yyyyMMdd}-{export.Id}.{renderer.FileExtension}";
            var publicId = storage.AllocatePublicId(export.OperatorUserId, export.Id);
            var uploadResult = await storage.UploadAsync(
                new RevenueExportStorageUpload(publicId, renderer.ContentType, bytes), cancellationToken);

            if (uploadResult.IsSuccess)
            {
                var expiresAt = clock.UtcNow.AddDays(options.RetentionDays);
                export.MarkReady(
                    fileName, uploadResult.StorageKey!, aggregate.AllTourDetails.Count, expiresAt, clock.UtcNow);
            }
            else
            {
                export.MarkFailed(uploadResult.SafeErrorCode ?? "storage_unavailable", clock.UtcNow);
            }
        }
        catch (Exception)
        {
            // No partial asset is ever referenced: the record only moves to Ready after a
            // successful upload above, so a thrown exception here always lands on MarkFailed.
            export.MarkFailed("render_error", clock.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}