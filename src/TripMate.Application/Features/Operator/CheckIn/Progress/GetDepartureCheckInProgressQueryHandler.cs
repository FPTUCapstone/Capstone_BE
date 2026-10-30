using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.CheckIn.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.CheckIn.Progress;

public sealed class GetDepartureCheckInProgressQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetDepartureCheckInProgressQuery, Result<DepartureProgressDto>>
{
    public async Task<Result<DepartureProgressDto>> Handle(
        GetDepartureCheckInProgressQuery request,
        CancellationToken cancellationToken)
    {
        var access = await OperatorBookingAccess.ResolveOperatorIdAsync(
            dbContext, currentUserService, cancellationToken);
        if (access.IsFailure)
        {
            return Result.Failure<DepartureProgressDto>(access.ErrorCode!, access.ErrorMessage!);
        }

        var operatorId = access.Value;

        var schedule = await dbContext.TourSchedules.AsNoTracking()
            .Where(s => s.Id == request.ScheduleId)
            .Select(s => new
            {
                s.Id,
                TourTitle = s.Tour.Title,
                s.StartAtUtc,
                s.EndAtUtc,
                s.MeetingPoint,
                OperatorUserId = s.Tour.OperatorUserId,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (schedule is null)
        {
            return Result.Failure<DepartureProgressDto>(
                CheckInErrorCodes.ScheduleNotFound, "Departure schedule was not found.");
        }

        if (schedule.OperatorUserId != operatorId)
        {
            return Result.Failure<DepartureProgressDto>(
                CheckInErrorCodes.ScheduleForbidden, "You do not have permission to view progress for this departure.");
        }

        var totalEligible = await dbContext.Bookings.AsNoTracking()
            .Where(b => b.TourScheduleId == request.ScheduleId
                && b.Status == BookingStatus.Confirmed
                && b.PaymentStatus == PaymentStatus.Paid)
            .SumAsync(b => (int?)b.Quantity, cancellationToken) ?? 0;

        var checkedIn = await (
            from b in dbContext.Bookings.AsNoTracking()
            where b.TourScheduleId == request.ScheduleId
                && b.Status == BookingStatus.Confirmed
                && b.PaymentStatus == PaymentStatus.Paid
            join t in dbContext.Tickets.AsNoTracking() on b.Id equals t.BookingId
            where t.Status == TicketStatus.Used
            select (int?)b.Quantity
        ).SumAsync(cancellationToken) ?? 0;

        var waiting = Math.Max(0, totalEligible - checkedIn);

        return Result.Success(new DepartureProgressDto(
            schedule.Id,
            schedule.TourTitle,
            schedule.StartAtUtc,
            schedule.EndAtUtc,
            schedule.MeetingPoint,
            checkedIn,
            totalEligible,
            waiting));
    }
}