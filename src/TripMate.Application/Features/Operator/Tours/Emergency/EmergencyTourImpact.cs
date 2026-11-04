using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency;

internal sealed record EmergencyTourImpactSnapshot(
    TourSchedule Schedule,
    Booking[] Bookings,
    TripSession[] ActiveSessions,
    string Version,
    bool HasUnverifiedSessions = false);

internal static class EmergencyTourImpact
{
    public static async Task<EmergencyTourImpactSnapshot> LoadAsync(
        IApplicationDbContext dbContext,
        TourSchedule schedule,
        CancellationToken cancellationToken)
    {
        Booking[] bookings = await dbContext.Bookings
            .Include(booking => booking.Tickets)
            .Where(booking => booking.TourScheduleId == schedule.Id
                && (booking.Status == BookingStatus.PendingPayment
                    || booking.Status == BookingStatus.Confirmed))
            .OrderBy(booking => booking.Id)
            .ToArrayAsync(cancellationToken);
        long[] bookingIds = bookings.Select(booking => booking.Id).ToArray();
        long[] travelerIds = bookings.Select(booking => booking.TravelerUserId).Distinct().ToArray();

        // Exact commercial session identity: Itinerary.SourceBookingId -> Booking.Id -> Booking.TourScheduleId
        TripSession[] sessions = await dbContext.TripSessions
            .Include(session => session.Itinerary)
            .Where(session => session.Itinerary.SourceBookingId != null
                && bookingIds.Contains(session.Itinerary.SourceBookingId.Value)
                && session.EndedAtUtc == null
                && session.FsmState != TripSession.CompletedState
                && session.FsmState != TripSession.InterruptedState)
            .OrderBy(session => session.Id)
            .ToArrayAsync(cancellationToken);

        bool hasUnverifiedSessions = await dbContext.TripSessions
            .Include(session => session.Itinerary)
            .AnyAsync(session => travelerIds.Contains(session.TravelerUserId)
                && session.EndedAtUtc == null
                && session.StartedAtUtc < schedule.EndAtUtc
                && session.ExpiresAtUtc > schedule.StartAtUtc
                && session.Itinerary.SourceTourId == schedule.TourId
                && session.Itinerary.SourceBookingId == null,
                cancellationToken);

        string canonical = string.Join('|',
            schedule.Id, schedule.Status, schedule.ReservedCapacity,
            string.Join(';', bookings.Select(booking =>
                $"{booking.Id}:{booking.Status}:{booking.PaymentStatus}:{booking.Quantity}:"
                + $"{booking.TotalAmount}:{booking.CancellationPolicySnapshot}:"
                + string.Join(',', booking.Tickets.OrderBy(ticket => ticket.Id)
                    .Select(ticket => $"{ticket.Id}:{ticket.Status}")))),
            string.Join(';', sessions.Select(session => $"{session.Id}:{session.FsmState}")),
            hasUnverifiedSessions ? "unverified-sessions" : "verified-sessions");
        string version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return new EmergencyTourImpactSnapshot(schedule, bookings, sessions, version, hasUnverifiedSessions);
    }

}
