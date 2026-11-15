using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Emergency.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Emergency;

public sealed record GetTourBookingEmergencyStatusQuery(long BookingId)
    : IRequest<Result<TourBookingEmergencyStatusResponse>>;

/// <summary>How the emergency decision handled the booking's payment (SRS UC-66).</summary>
public static class EmergencyRefundHandling
{
    /// <summary>The decision submitted a refund for this booking; progress is on the refund endpoint.</summary>
    public const string RefundSubmitted = "RefundSubmitted";

    /// <summary>The booking was paid and TripMate reviews the refund case by case.</summary>
    public const string UnderReview = "UnderReview";

    /// <summary>Nothing was paid, so nothing is refunded.</summary>
    public const string NotApplicable = "NotApplicable";
}

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
    DateTimeOffset? AcknowledgedAtUtc = null,
    string RefundHandling = EmergencyRefundHandling.NotApplicable,
    long? TourId = null);

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
                booking.TourSchedule.TourId,
                BookingStatus = booking.Status,
                ScheduleStatus = booking.TourSchedule.Status,
                TotalTicketCount = booking.Tickets.Count,
                CancelledTicketCount = booking.Tickets.Count(ticket => ticket.Status == TicketStatus.Cancelled),
                UsedTicketCount = booking.Tickets.Count(ticket => ticket.Status == TicketStatus.Used),
                booking.EmergencyNoticeAcknowledgedAtUtc,
                booking.PaymentStatus,
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

        string refundHandling = await EmergencyRefundHandoff.HasSubmittedRefundAsync(
                dbContext, operation, result.Id, cancellationToken)
            ? EmergencyRefundHandling.RefundSubmitted
            : result.PaymentStatus == PaymentStatus.Unpaid
                ? EmergencyRefundHandling.NotApplicable
                : EmergencyRefundHandling.UnderReview;

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
            result.EmergencyNoticeAcknowledgedAtUtc,
            refundHandling,
            result.TourId));
    }

    private static Result<TourBookingEmergencyStatusResponse> Failure(string code, string message) =>
        Result.Failure<TourBookingEmergencyStatusResponse>(code, message);
}