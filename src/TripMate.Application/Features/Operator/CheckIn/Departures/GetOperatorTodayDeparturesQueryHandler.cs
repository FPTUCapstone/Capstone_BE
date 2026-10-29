using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.CheckIn.Common;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.CheckIn.Departures;

public sealed class GetOperatorTodayDeparturesQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider clock)
    : IRequestHandler<GetOperatorTodayDeparturesQuery, Result<IReadOnlyList<OperatorDepartureSummaryDto>>>
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task<Result<IReadOnlyList<OperatorDepartureSummaryDto>>> Handle(
        GetOperatorTodayDeparturesQuery request,
        CancellationToken cancellationToken)
    {
        var access = await OperatorBookingAccess.ResolveOperatorIdAsync(
            dbContext, currentUserService, cancellationToken);
        if (access.IsFailure)
        {
            return Result.Failure<IReadOnlyList<OperatorDepartureSummaryDto>>(access.ErrorCode!, access.ErrorMessage!);
        }

        var operatorId = access.Value;
        var now = clock.UtcNow;
        var localDate = request.TargetDate ?? DateOnly.FromDateTime(now.ToOffset(VietnamOffset).DateTime);

        var localMidnight = new DateTimeOffset(localDate.ToDateTime(TimeOnly.MinValue), VietnamOffset);
        var windowStartUtc = localMidnight.ToUniversalTime();
        var windowEndUtc = windowStartUtc.AddDays(1).AddTicks(-1);

        var schedules = await dbContext.TourSchedules.AsNoTracking()
            .Where(s => s.Tour.OperatorUserId == operatorId
                && s.StartAtUtc >= windowStartUtc
                && s.StartAtUtc <= windowEndUtc)
            .OrderBy(s => s.StartAtUtc)
            .Select(s => new
            {
                s.Id,
                s.TourId,
                TourTitle = s.Tour.Title,
                s.StartAtUtc,
                s.EndAtUtc,
                s.MeetingPoint,
                s.Status,
            })
            .ToListAsync(cancellationToken);

        var resultList = new List<OperatorDepartureSummaryDto>(schedules.Count);

        foreach (var s in schedules)
        {
            var totalEligible = await dbContext.Bookings.AsNoTracking()
                .Where(b => b.TourScheduleId == s.Id
                    && b.Status == BookingStatus.Confirmed
                    && b.PaymentStatus == PaymentStatus.Paid)
                .SumAsync(b => (int?)b.Quantity, cancellationToken) ?? 0;

            var checkedIn = await (
                from b in dbContext.Bookings.AsNoTracking()
                where b.TourScheduleId == s.Id
                    && b.Status == BookingStatus.Confirmed
                    && b.PaymentStatus == PaymentStatus.Paid
                join t in dbContext.Tickets.AsNoTracking() on b.Id equals t.BookingId
                where t.Status == TicketStatus.Used
                select (int?)b.Quantity
            ).SumAsync(cancellationToken) ?? 0;

            var waiting = Math.Max(0, totalEligible - checkedIn);

            resultList.Add(new OperatorDepartureSummaryDto(
                s.Id,
                s.TourId,
                s.TourTitle,
                s.StartAtUtc,
                s.EndAtUtc,
                s.MeetingPoint,
                s.Status.ToString(),
                checkedIn,
                totalEligible,
                waiting));
        }

        return Result.Success<IReadOnlyList<OperatorDepartureSummaryDto>>(resultList);
    }
}
