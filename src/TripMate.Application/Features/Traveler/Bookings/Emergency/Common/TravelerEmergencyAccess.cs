using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Emergency.Common;

internal static class TravelerEmergencyAccess
{
    public static Task<bool> IsActiveTravelerAsync(
        IApplicationDbContext dbContext,
        long travelerId,
        CancellationToken cancellationToken) => dbContext.Users.AnyAsync(user =>
            user.Id == travelerId
            && user.Role == UserRole.Traveler
            && user.Status == AccountStatus.Active, cancellationToken);

    /// <summary>
    /// A decision records one notice per booking it affected, so a booking the Traveler cancelled
    /// before a later decision on the same departure is not an affected booking.
    /// </summary>
    public static Task<bool> IsAffectedBookingAsync(
        IApplicationDbContext dbContext,
        long travelerId,
        long bookingId,
        CancellationToken cancellationToken) => dbContext.Notifications.AnyAsync(notice =>
            notice.UserId == travelerId
            && notice.RelatedEntityType == Notification.BookingRelatedEntityType
            && notice.RelatedEntityId == bookingId
            && (notice.Type == Notification.EmergencyDepartureCancelledType
                || notice.Type == Notification.EmergencyDepartureStoppedType), cancellationToken);
}
