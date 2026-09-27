using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourMedia.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;

using DomainAuditLog = TripMate.Domain.Entities.AuditLog;
using DomainTourMedia = TripMate.Domain.Entities.TourMedia;

namespace TripMate.Application.Features.TourMedia.Reorder;

public sealed class ReorderTourMediaCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    ITourMediaUploadLock tourMediaLock)
    : IRequestHandler<ReorderTourMediaCommand, Result<IReadOnlyList<TourMediaDto>>>
{
    public async Task<Result<IReadOnlyList<TourMediaDto>>> Handle(
        ReorderTourMediaCommand command,
        CancellationToken cancellationToken) =>
        await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            await tourMediaLock.AcquireTourAsync(command.TourId, transactionCancellationToken);
            TourMediaAccess access = await TourMediaAccessResolver.AuthorizeAsync(
                dbContext, command.TourId, command.CurrentUserId, transactionCancellationToken);
            if (!access.IsAuthorized)
            {
                return Result.Failure<IReadOnlyList<TourMediaDto>>(
                    access.FailureCode!, "The tour is unavailable.");
            }

            if (!TourMediaAccessResolver.AllowsMaterialChange(access.Tour!.Status))
            {
                return Result.Failure<IReadOnlyList<TourMediaDto>>(
                    TourMediaErrorCodes.TourMediaChangeLocked,
                    "The tour media cannot be changed in its current state.");
            }

            List<DomainTourMedia> active = await dbContext.TourMedia
                .Where(media => media.TourId == command.TourId &&
                    media.LifecycleStatus == TourMediaLifecycleStatus.Active)
                .OrderBy(media => media.SortOrder)
                .ThenBy(media => media.Id)
                .ToListAsync(transactionCancellationToken);
            if (!HasExactActiveSet(command, active))
            {
                return Result.Failure<IReadOnlyList<TourMediaDto>>(
                    TourMediaErrorCodes.InvalidCompleteOrder,
                    "The requested order must contain every active media item exactly once.");
            }

            var byId = active.ToDictionary(media => media.Id);
            List<DomainTourMedia> desired = command.MediaIds.Select(id => byId[id]).ToList();
            // Snapshot before phase one clears flags to satisfy the filtered unique index.
            long? primaryMediaId = command.PrimaryMediaId ??
                active.SingleOrDefault(media => media.IsPrimary)?.Id;
            var now = dateTimeProvider.UtcNow;

            if (active.Count == 0)
            {
                dbContext.AuditLogs.Add(DomainAuditLog.CreateRecordedOutcome(
                    command.CurrentUserId,
                    AuditActionTypes.TourMediaReordered,
                    AuditEntityTypes.TourMedia,
                    command.TourId,
                    now,
                    AuditOutcome.Success,
                    afterData: JsonSerializer.Serialize(new
                    {
                        command.TourId,
                        MediaIds = command.MediaIds,
                        command.PrimaryMediaId,
                    })));
                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return Result.Success<IReadOnlyList<TourMediaDto>>([]);
            }

            // Phase one moves every active row out of its final range before applying
            // the requested order, so SQL Server's filtered unique index is never
            // transiently violated by a row swap.
            int temporaryStart = checked(active.Max(media => media.SortOrder) + active.Count + 1);
            for (var index = 0; index < active.Count; index++)
            {
                active[index].SetOrderAndPrimary(temporaryStart + index, false, now);
            }
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            for (var index = 0; index < desired.Count; index++)
            {
                desired[index].SetOrderAndPrimary(
                    index + 1,
                    primaryMediaId == desired[index].Id,
                    now);
            }
            dbContext.AuditLogs.Add(DomainAuditLog.CreateRecordedOutcome(
                command.CurrentUserId,
                AuditActionTypes.TourMediaReordered,
                AuditEntityTypes.TourMedia,
                command.TourId,
                now,
                AuditOutcome.Success,
                afterData: JsonSerializer.Serialize(new
                {
                    command.TourId,
                    MediaIds = command.MediaIds,
                    command.PrimaryMediaId,
                })));
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return Result.Success<IReadOnlyList<TourMediaDto>>(desired.Select(TourMediaProjection.ToDto).ToList());
        }, cancellationToken);

    private static bool HasExactActiveSet(
        ReorderTourMediaCommand command,
        IReadOnlyCollection<DomainTourMedia> active) =>
        command.MediaIds.Count == active.Count &&
        command.MediaIds.Distinct().Count() == command.MediaIds.Count &&
        command.MediaIds.All(id => active.Any(media => media.Id == id)) &&
        (command.PrimaryMediaId is null || command.MediaIds.Contains(command.PrimaryMediaId.Value));
}