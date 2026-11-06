using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency;

public sealed class GetEmergencyTourImpactQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<GetEmergencyTourImpactQuery, Result<EmergencyTourImpactResponse>>
{
    public async Task<Result<EmergencyTourImpactResponse>> Handle(
        GetEmergencyTourImpactQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId
            || currentUser.Role != nameof(UserRole.TourOperator))
            return Result.Failure<EmergencyTourImpactResponse>(EmergencyTourErrorCodes.Forbidden,
                "Cần quyền đơn vị điều hành tour.");

        bool operatorIsEligible = await EmergencyOperatorAccess.IsEligibleOperatorAsync(
            dbContext, operatorId, cancellationToken);
        if (!operatorIsEligible)
            return Result.Failure<EmergencyTourImpactResponse>(EmergencyTourErrorCodes.Forbidden,
                "Cần tài khoản đơn vị điều hành đang hoạt động và đã được duyệt.");

        var schedule = await dbContext.TourSchedules
            .Include(candidate => candidate.Tour)
            .SingleOrDefaultAsync(candidate => candidate.Id == request.ScheduleId
                && candidate.TourId == request.TourId
                && candidate.Tour.OperatorUserId == operatorId, cancellationToken);
        if (schedule is null)
            return Result.Failure<EmergencyTourImpactResponse>(EmergencyTourErrorCodes.NotFound,
                "Không tìm thấy lịch khởi hành.");
        if (schedule.Status == TourScheduleStatus.Completed || clock.UtcNow >= schedule.EndAtUtc)
            return Result.Failure<EmergencyTourImpactResponse>(EmergencyTourErrorCodes.AlreadyCompleted,
                "Lịch khởi hành đã hoàn thành nên không thể hủy hoặc dừng.");
        if (schedule.Status != TourScheduleStatus.Scheduled)
            return Result.Failure<EmergencyTourImpactResponse>(EmergencyTourErrorCodes.AlreadyTerminal,
                "Lịch khởi hành này đã có quyết định cuối cùng.");

        var impact = await EmergencyTourImpact.LoadAsync(dbContext, schedule, cancellationToken);
        bool active = clock.UtcNow >= schedule.StartAtUtc;

        return Result.Success(new EmergencyTourImpactResponse(
            request.TourId,
            schedule.Id,
            active
                ? EmergencyTourCancellationOperation.EmergencyStopActiveAction
                : EmergencyTourCancellationOperation.CancelBeforeDepartureAction,
            schedule.Status.ToString(),
            impact.Bookings.Count(booking => booking.PaymentStatus == PaymentStatus.Paid),
            impact.Bookings.Count(booking => booking.PaymentStatus != PaymentStatus.Paid),
            impact.Bookings.Sum(booking => booking.Quantity),
            impact.ActiveSessions.Length,
            impact.Bookings.SelectMany(booking => booking.Tickets)
                .Count(ticket => ticket.Status == TicketStatus.Valid),
            impact.HasUnverifiedSessions,
            impact.Version));
    }
}