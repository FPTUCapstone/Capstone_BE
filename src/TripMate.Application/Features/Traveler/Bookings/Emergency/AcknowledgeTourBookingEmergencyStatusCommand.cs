using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Emergency.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Emergency;

public sealed record AcknowledgeTourBookingEmergencyStatusCommand(long BookingId)
    : IRequest<Result<TourBookingEmergencyAcknowledgeResponse>>;

public sealed record TourBookingEmergencyAcknowledgeResponse(
    long BookingId,
    DateTimeOffset AcknowledgedAtUtc);

public sealed class AcknowledgeTourBookingEmergencyStatusCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<AcknowledgeTourBookingEmergencyStatusCommand, Result<TourBookingEmergencyAcknowledgeResponse>>
{
    public async Task<Result<TourBookingEmergencyAcknowledgeResponse>> Handle(
        AcknowledgeTourBookingEmergencyStatusCommand request,
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

        var booking = await dbContext.Bookings
            .SingleOrDefaultAsync(b => b.Id == request.BookingId
                && b.TravelerUserId == travelerId, cancellationToken);
        if (booking is null)
            return Failure(TourBookingErrorCodes.BookingNotFound,
                "Không tìm thấy đơn đặt tour.");

        if (!await TravelerEmergencyAccess.IsAffectedBookingAsync(
                dbContext, travelerId, booking.Id, cancellationToken))
            return Failure(TourBookingErrorCodes.EmergencyStatusNotFound,
                "Đơn đặt tour này chưa có quyết định khẩn cấp nào.");
        var operation = await dbContext.EmergencyTourCancellationOperations.AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                candidate.TourScheduleId == booking.TourScheduleId, cancellationToken);
        if (operation is null)
            return Failure(TourBookingErrorCodes.EmergencyStatusNotFound,
                "Đơn đặt tour này chưa có quyết định khẩn cấp nào.");

        DateTimeOffset now = clock.UtcNow;
        booking.AcknowledgeEmergencyNotice(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new TourBookingEmergencyAcknowledgeResponse(
            booking.Id,
            booking.EmergencyNoticeAcknowledgedAtUtc ?? now));
    }

    private static Result<TourBookingEmergencyAcknowledgeResponse> Failure(string code, string message) =>
        Result.Failure<TourBookingEmergencyAcknowledgeResponse>(code, message);
}