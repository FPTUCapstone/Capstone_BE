using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Emergency.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Emergency;

/// <summary>
/// Lets a Traveler discover emergency notices when a push was denied or missed. Trip History pages
/// one tab at a time, so it cannot answer "is any notice unread?". Discovery data only: the detail
/// stays behind the owner-checked emergency status endpoint.
/// </summary>
public sealed record ListTourBookingEmergencyNoticesQuery
    : IRequest<Result<IReadOnlyList<TourBookingEmergencyNoticeResponse>>>;

public sealed record TourBookingEmergencyNoticeResponse(
    long BookingId,
    long ScheduleId,
    string Action,
    DateTimeOffset CommittedAtUtc,
    DateTimeOffset? AcknowledgedAtUtc,
    bool RequiresAcknowledgement);

public sealed class ListTourBookingEmergencyNoticesQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<ListTourBookingEmergencyNoticesQuery, Result<IReadOnlyList<TourBookingEmergencyNoticeResponse>>>
{
    public async Task<Result<IReadOnlyList<TourBookingEmergencyNoticeResponse>>> Handle(
        ListTourBookingEmergencyNoticesQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId
            || currentUser.Role != nameof(UserRole.Traveler)
            || !await TravelerEmergencyAccess.IsActiveTravelerAsync(dbContext, travelerId, cancellationToken))
        {
            return Result.Failure<IReadOnlyList<TourBookingEmergencyNoticeResponse>>(
                TourBookingErrorCodes.AccessRequired,
                "An active Traveler account is required.");
        }

        // The decision records one notice per booking it affected, so a booking cancelled on its
        // own before a later decision on the same departure is not reported as affected.
        var rows = await (
                from booking in dbContext.Bookings.AsNoTracking()
                join operation in dbContext.EmergencyTourCancellationOperations.AsNoTracking()
                    on booking.TourScheduleId equals operation.TourScheduleId
                where booking.TravelerUserId == travelerId
                    && dbContext.Notifications.Any(notice =>
                        notice.UserId == travelerId
                        && notice.RelatedEntityType == Notification.BookingRelatedEntityType
                        && notice.RelatedEntityId == booking.Id
                        && (notice.Type == Notification.EmergencyDepartureCancelledType
                            || notice.Type == Notification.EmergencyDepartureStoppedType))
                orderby operation.CommittedAtUtc descending, booking.Id descending
                select new
                {
                    booking.Id,
                    booking.TourScheduleId,
                    operation.Action,
                    operation.CommittedAtUtc,
                    booking.EmergencyNoticeAcknowledgedAtUtc,
                })
            .ToListAsync(cancellationToken);

        IReadOnlyList<TourBookingEmergencyNoticeResponse> notices = rows
            .Select(row => new TourBookingEmergencyNoticeResponse(
                row.Id,
                row.TourScheduleId,
                row.Action,
                row.CommittedAtUtc,
                row.EmergencyNoticeAcknowledgedAtUtc,
                row.EmergencyNoticeAcknowledgedAtUtc is null))
            .ToList();
        return Result.Success(notices);
    }
}
