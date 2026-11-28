using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Delete;

public sealed class DeleteOperatorTourCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<DeleteOperatorTourCommand, Result>
{
    public async Task<Result> Handle(DeleteOperatorTourCommand request, CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue)
            return Result.Failure(OperatorTourErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");

        long operatorId = currentUser.UserId.Value;
        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            bool eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
                profile.UserId == operatorId &&
                profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
                profile.User.Role == UserRole.TourOperator &&
                profile.User.Status == AccountStatus.Active,
                transactionCancellationToken);
            if (!eligible)
                return Result.Failure(OperatorTourErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");

            var tour = await dbContext.Tours.SingleOrDefaultAsync(
                t => t.Id == request.TourId && t.OperatorUserId == operatorId,
                transactionCancellationToken);
            if (tour is null) return Result.Failure(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
            if (tour.Status is not (TourStatus.Draft or TourStatus.Rejected))
                return Result.Failure(OperatorTourErrorCodes.InvalidState, "Only draft or rejected tours can be deleted.");

            bool hasChildRevisions = await dbContext.Tours.AsNoTracking().AnyAsync(
                candidate => candidate.VersionRootTourId == tour.Id && candidate.Id != tour.Id,
                transactionCancellationToken);
            if (hasChildRevisions)
            {
                return Result.Failure(
                    OperatorTourErrorCodes.InvalidState,
                    "A tour with descendant revisions cannot be deleted.");
            }

            DateTimeOffset now = clock.UtcNow;

            var createOps = await dbContext.TourPackageCreateOperations
                .Where(op => op.TourId == tour.Id)
                .ToListAsync(transactionCancellationToken);
            if (createOps.Count > 0)
            {
                dbContext.TourPackageCreateOperations.RemoveRange(createOps);
            }

            var activeMediaPublicIds = await dbContext.TourMedia
                .Where(media => media.TourId == tour.Id && media.LifecycleStatus == TourMediaLifecycleStatus.Active)
                .Select(media => media.CloudinaryPublicId)
                .ToListAsync(transactionCancellationToken);
            if (activeMediaPublicIds.Count > 0)
            {
                var queuedPublicIds = await dbContext.TourMediaCleanupOutbox
                    .Where(item => activeMediaPublicIds.Contains(item.CloudinaryPublicId))
                    .Select(item => item.CloudinaryPublicId)
                    .ToListAsync(transactionCancellationToken);
                var queuedSet = queuedPublicIds.ToHashSet(StringComparer.Ordinal);
                foreach (string publicId in activeMediaPublicIds.Where(id => !queuedSet.Contains(id)))
                {
                    dbContext.TourMediaCleanupOutbox.Add(
                        TourMediaCleanupOutboxItem.CreateForOrphanedProviderAsset(publicId, now));
                }
            }

            if (tour.VersionRootTourId == tour.Id)
            {
                tour.ClearVersionRootForDelete();
                await dbContext.SaveChangesAsync(transactionCancellationToken);
            }

            // Schedules deliberately use a database NO ACTION relation because a sold schedule must
            // never disappear. Draft/rejected schedules have no bookings and are removed explicitly.
            dbContext.TourSchedules.RemoveRange(dbContext.TourSchedules.Where(schedule => schedule.TourId == tour.Id));
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                operatorId, AuditActionTypes.TourDeleted, AuditEntityTypes.Tour, tour.Id, now,
                AuditOutcome.Success,
                beforeData: JsonSerializer.Serialize(new { tour.Title, Status = tour.Status.ToString(), tour.VersionNo })));
            dbContext.Tours.Remove(tour);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return Result.Success();
        }, cancellationToken);
    }
}