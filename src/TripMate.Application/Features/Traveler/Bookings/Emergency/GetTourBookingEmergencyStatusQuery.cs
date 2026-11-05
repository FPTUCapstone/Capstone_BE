using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Emergency.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Emergency;

public sealed record GetTourBookingEmergencyStatusQuery(long BookingId)
    : IRequest<Result<TourBookingEmergencyStatusResponse>>;

public sealed record TourBookingEmergencyStatusResponse(
    long BookingId,
    long TourScheduleId,
    string BookingStatus,
    string ScheduleStatus,
    int TotalTicketCount,
    int CancelledTicketCount,
    int UsedTicketCount,
    string Action,
    string ReasonCategory,
    string ReasonDetails,
    string? ImmediateInstructions,
    string? EmergencyContact,
    DateTimeOffset CommittedAtUtc,
    DateTimeOffset? AcknowledgedAtUtc = null);

public sealed class GetTourBookingEmergencyStatusQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<GetTourBookingEmergencyStatusQuery, Result<TourBookingEmergencyStatusResponse>>
{
    public async Task<Result<TourBookingEmergencyStatusResponse>> Handle(
        GetTourBookingEmergencyStatusQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId
            || currentUser.Role != nameof(UserRole.Traveler))
            return Failure(TourBookingErrorCodes.AccessRequired,
                "Vui lòng đăng nhập bằng tài khoản du khách.");

        bool travelerIsActive = await TravelerEmergencyAccess.IsActiveTravelerAsync(
            dbContext, travelerId, cancellationToken);
        if (!travelerIsActive)
            return Failure(TourBookingErrorCodes.AccessRequired,
                "Cần tài khoản du khách đang hoạt động.");

        var result = await dbContext.Bookings.AsNoTracking()
            .Where(booking => booking.Id == request.BookingId
                && booking.TravelerUserId == travelerId)
            .Select(booking => new
            {
                booking.Id,
                booking.TourScheduleId,
                BookingStatus = booking.Status,
                ScheduleStatus = booking.TourSchedule.Status,
                TotalTicketCount = booking.Tickets.Count,
                CancelledTicketCount = booking.Tickets.Count(ticket => ticket.Status == TicketStatus.Cancelled),
                UsedTicketCount = booking.Tickets.Count(ticket => ticket.Status == TicketStatus.Used),
                booking.EmergencyNoticeAcknowledgedAtUtc,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (result is null)
            return Failure(TourBookingErrorCodes.BookingNotFound,
                "Không tìm thấy đơn đặt tour.");
        if (!await TravelerEmergencyAccess.IsAffectedBookingAsync(
                dbContext, travelerId, result.Id, cancellationToken))
            return Failure(TourBookingErrorCodes.EmergencyStatusNotFound,
                "Đơn đặt tour này chưa có quyết định khẩn cấp nào.");
        var operation = await dbContext.EmergencyTourCancellationOperations.AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                candidate.TourScheduleId == result.TourScheduleId, cancellationToken);
        if (operation is null)
            return Failure(TourBookingErrorCodes.EmergencyStatusNotFound,
                "Đơn đặt tour này chưa có quyết định khẩn cấp nào.");

        return Result.Success(new TourBookingEmergencyStatusResponse(
            result.Id,
            operation.TourScheduleId,
            result.BookingStatus.ToString(),
            result.ScheduleStatus.ToString(),
            result.TotalTicketCount,
            result.CancelledTicketCount,
            result.UsedTicketCount,
            operation.Action,
            operation.ReasonCategory,
            operation.ReasonDetails,
            operation.ImmediateInstructions,
            operation.EmergencyContact,
            operation.CommittedAtUtc,
            result.EmergencyNoticeAcknowledgedAtUtc));
    }

    private static Result<TourBookingEmergencyStatusResponse> Failure(string code, string message) =>
        Result.Failure<TourBookingEmergencyStatusResponse>(code, message);
}
