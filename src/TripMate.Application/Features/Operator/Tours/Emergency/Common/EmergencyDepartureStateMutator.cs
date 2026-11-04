using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency.Common;

/// <summary>
/// Encapsulates the shared domain state transitions for departures, bookings, tickets, and sessions
/// for a manual Tour Operator emergency decision.
/// Has zero provider or notification policy knowledge.
/// </summary>
public static class EmergencyDepartureStateMutator
{
    public static async Task<bool> MutateAsync(
        IApplicationDbContext dbContext,
        TourSchedule schedule,
        IReadOnlyCollection<Booking> bookings,
        IReadOnlyCollection<TripSession> activeSessions,
        bool isPreStart,
        DateTimeOffset now,
        string bookingReason,
        string? sessionReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(bookings);

        bool stateChanged = isPreStart
            ? schedule.CancelBeforeDeparture(now)
            : schedule.EmergencyStop(now);

        if (!stateChanged)
        {
            return false;
        }

        foreach (Booking booking in bookings)
        {
            if (isPreStart)
            {
                booking.Cancel(now, bookingReason);
                schedule.ReleaseCapacity(booking.Quantity);
            }
            else
            {
                booking.Interrupt(now, bookingReason);
            }

            foreach (Ticket ticket in booking.Tickets.Where(ticket => ticket.Status != TicketStatus.Used))
            {
                ticket.Cancel();
            }

            await dbContext.RevokeActiveTicketQrCodesAsync(booking.Id, now, cancellationToken);
        }

        foreach (TripSession session in activeSessions)
        {
            session.InterruptForEmergency(now, sessionReason ?? "Emergency");
        }

        return true;
    }
}
