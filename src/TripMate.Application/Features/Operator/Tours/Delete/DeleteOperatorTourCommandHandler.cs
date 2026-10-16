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
        bool eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
            profile.UserId == operatorId &&
            profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
            profile.User.Role == UserRole.TourOperator &&
            profile.User.Status == AccountStatus.Active,
            cancellationToken);
        if (!eligible)
            return Result.Failure(OperatorTourErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");

        var tour = await dbContext.Tours.SingleOrDefaultAsync(t => t.Id == request.TourId && t.OperatorUserId == operatorId, cancellationToken);
        if (tour is null) return Result.Failure(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
        if (tour.Status is not (TourStatus.Draft or TourStatus.Rejected))
            return Result.Failure(OperatorTourErrorCodes.InvalidState, "Only draft or rejected tours can be deleted.");

        // Schedules deliberately use a database NO ACTION relation because a sold schedule must
        // never disappear. Draft/rejected schedules have no bookings and are removed explicitly.
        dbContext.TourSchedules.RemoveRange(dbContext.TourSchedules.Where(schedule => schedule.TourId == tour.Id));
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            operatorId, AuditActionTypes.TourDeleted, AuditEntityTypes.Tour, tour.Id, clock.UtcNow,
            AuditOutcome.Success,
            beforeData: JsonSerializer.Serialize(new { tour.Title, Status = tour.Status.ToString(), tour.VersionNo })));
        dbContext.Tours.Remove(tour);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}