using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourMedia.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;

using DomainAuditLog = TripMate.Domain.Entities.AuditLog;
using DomainCleanupOutbox = TripMate.Domain.Entities.TourMediaCleanupOutboxItem;
using DomainTourMedia = TripMate.Domain.Entities.TourMedia;

namespace TripMate.Application.Features.TourMedia.Delete;

public sealed class DeleteTourMediaCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    ITourMediaUploadLock tourMediaLock)
    : IRequestHandler<DeleteTourMediaCommand, Result>
{
    public static readonly TimeSpan CleanupRetention = TimeSpan.FromDays(30);

    public async Task<Result> Handle(DeleteTourMediaCommand command, CancellationToken cancellationToken) =>
        await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            await tourMediaLock.AcquireTourAsync(command.TourId, transactionCancellationToken);
            TourMediaAccess access = await TourMediaAccessResolver.AuthorizeAsync(
                dbContext, command.TourId, command.CurrentUserId, transactionCancellationToken);
            if (!access.IsAuthorized)
            {
                return Result.Failure(access.FailureCode!, "The tour is unavailable.");
            }

            DomainTourMedia? media = await dbContext.TourMedia.IgnoreQueryFilters().SingleOrDefaultAsync(candidate =>
                candidate.Id == command.TourMediaId && candidate.TourId == command.TourId,
                transactionCancellationToken);
            if (media is null)
            {
                return Result.Failure(TourMediaErrorCodes.TourMediaNotFound, "The media is unavailable.");
            }

            // Repeating an already completed normal removal is explicitly idempotent.
            if (media.LifecycleStatus == TourMediaLifecycleStatus.Deleted)
            {
                return Result.Success();
            }

            if (!TourMediaAccessResolver.AllowsMaterialChange(access.Tour!.Status))
            {
                return Result.Failure(
                    TourMediaErrorCodes.TourMediaChangeLocked,
                    "The tour media cannot be changed in its current state.");
            }

            var now = dateTimeProvider.UtcNow;
            int previousOrder = media.SortOrder;
            bool wasPrimary = media.IsPrimary;
            media.SoftDelete(now);
            // Persist the lifecycle transition before querying the remaining active
            // set. This keeps the deleted tracked entity out of the query in both
            // SQL Server and the in-memory test provider; the enclosing database
            // transaction still makes the whole workflow atomic in production.
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            List<DomainTourMedia> remaining = await dbContext.TourMedia
                .Where(candidate => candidate.TourId == command.TourId &&
                    candidate.LifecycleStatus == TourMediaLifecycleStatus.Active)
                .OrderBy(candidate => candidate.SortOrder)
                .ThenBy(candidate => candidate.Id)
                .ToListAsync(transactionCancellationToken);
            for (var index = 0; index < remaining.Count; index++)
            {
                remaining[index].SetOrderAndPrimary(
                    index + 1,
                    remaining[index].IsPrimary,
                    now);
            }

            dbContext.TourMediaCleanupOutbox.Add(DomainCleanupOutbox.Create(
                media,
                now.Add(CleanupRetention),
                now));
            dbContext.AuditLogs.Add(DomainAuditLog.CreateRecordedOutcome(
                command.CurrentUserId,
                AuditActionTypes.TourMediaDeleted,
                AuditEntityTypes.TourMedia,
                media.Id,
                now,
                AuditOutcome.Success,
                beforeData: JsonSerializer.Serialize(new { SortOrder = previousOrder, IsPrimary = wasPrimary }),
                afterData: JsonSerializer.Serialize(new
                {
                    LifecycleStatus = nameof(TourMediaLifecycleStatus.Deleted),
                    DeletedAtUtc = now,
                })));
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return Result.Success();
        }, cancellationToken);
}