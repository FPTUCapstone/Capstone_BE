using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>
/// Turns the keys of one page into cards (spec TM-78 P6, P7, P8, A3, A6, T1, T2, T4). One batched query
/// per related table, never one per record. Records that do not belong to the caller are dropped, so a
/// key can never expose another Traveler's data. Output follows the key order.
/// </summary>
public static class TripCardHydrator
{
    public static async Task<IReadOnlyList<TripHistoryItemResponse>> HydrateAsync(
        IApplicationDbContext dbContext,
        long travelerUserId,
        string tab,
        IReadOnlyList<TripKey> keys,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var bookingIds = IdsOf(keys, TripRecordKinds.CommerceBooking);
        var itineraryIds = IdsOf(keys, TripRecordKinds.Itinerary);
        var bookings = await LoadBookingsAsync(dbContext, travelerUserId, bookingIds, cancellationToken);
        var itineraries = await LoadItinerariesAsync(
            dbContext, travelerUserId, tab, itineraryIds, nowUtc, cancellationToken);

        var cards = new List<TripHistoryItemResponse>(keys.Count);
        foreach (var key in keys)
        {
            if (key.Kind == TripRecordKinds.CommerceBooking && bookings.TryGetValue(key.Id, out var booking))
            {
                cards.Add(booking.ToCard(tab, key));
            }
            else if (key.Kind == TripRecordKinds.Itinerary && itineraries.TryGetValue(key.Id, out var itinerary))
            {
                cards.Add(itinerary.ToCard(tab, key));
            }
        }

        return cards;
    }

    private static long[] IdsOf(IReadOnlyList<TripKey> keys, string kind) =>
        keys.Where(key => key.Kind == kind).Select(key => key.Id).Distinct().ToArray();

    private static async Task<Dictionary<long, BookingData>> LoadBookingsAsync(
        IApplicationDbContext dbContext,
        long travelerUserId,
        long[] requestedIds,
        CancellationToken cancellationToken)
    {
        if (requestedIds.Length == 0)
        {
            return [];
        }

        var rows = await dbContext.Bookings.AsNoTracking()
            .Where(booking => requestedIds.Contains(booking.Id) && booking.TravelerUserId == travelerUserId)
            .Select(booking => new
            {
                booking.Id,
                booking.BookingCode,
                booking.Quantity,
                booking.TotalAmount,
                booking.Status,
                booking.TourSchedule.Tour.Title,
            })
            .ToListAsync(cancellationToken);
        var ids = rows.Select(row => row.Id).ToArray();

        // T2: Valid before Used, then the lowest ticket id. Cancelled and Expired are never eligible.
        var tickets = (await dbContext.Tickets.AsNoTracking()
                .Where(ticket => ids.Contains(ticket.BookingId)
                    && (ticket.Status == TicketStatus.Valid || ticket.Status == TicketStatus.Used))
                .Select(ticket => new { ticket.BookingId, ticket.Id, ticket.Status })
                .ToListAsync(cancellationToken))
            .GroupBy(ticket => ticket.BookingId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(ticket => ticket.Status == TicketStatus.Valid ? 0 : 1)
                    .ThenBy(ticket => ticket.Id)
                    .First().Id);

        var refunded = (await dbContext.Refunds.AsNoTracking()
                .Where(refund => ids.Contains(refund.BookingId))
                .Select(refund => refund.BookingId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // A6: one review per booking is normal; if legacy data has several, the lowest id is reported.
        var reviews = (await dbContext.TripReviews.AsNoTracking()
                .Where(review => review.BookingId != null && ids.Contains(review.BookingId.Value))
                .Select(review => new { BookingId = review.BookingId!.Value, review.Id, review.OverallRating })
                .ToListAsync(cancellationToken))
            .GroupBy(review => review.BookingId)
            .ToDictionary(group => group.Key, group => group.OrderBy(review => review.Id).First());

        return rows.ToDictionary(
            row => row.Id,
            row => new BookingData(
                row.Id,
                row.BookingCode,
                row.Quantity,
                row.TotalAmount,
                row.Status,
                row.Title,
                tickets.TryGetValue(row.Id, out var ticketId) ? ticketId : null,
                refunded.Contains(row.Id),
                reviews.TryGetValue(row.Id, out var review) ? (review.Id, (int)review.OverallRating) : null));
    }

    private static async Task<Dictionary<long, ItineraryData>> LoadItinerariesAsync(
        IApplicationDbContext dbContext,
        long travelerUserId,
        string tab,
        long[] requestedIds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (requestedIds.Length == 0)
        {
            return [];
        }

        var rows = await dbContext.Itineraries.AsNoTracking()
            .Where(itinerary => requestedIds.Contains(itinerary.Id) && itinerary.TravelerUserId == travelerUserId)
            .Select(itinerary => new
            {
                itinerary.Id,
                itinerary.Title,
                itinerary.Status,
                PlannedStops = itinerary.Items.Count(item => item.Kind == ItineraryItemKind.Visit),
            })
            .ToListAsync(cancellationToken);

        // A3: a Completed trip is counted against the frozen snapshot of its representative session.
        var sessionCounts = new Dictionary<long, (int Stops, int Visited)>();
        if (tab == ListTravelerTripsQuery.TabCompleted)
        {
            var ids = rows.Select(row => row.Id).ToArray();
            var sessions = await dbContext.TripSessions.AsNoTracking()
                .RepresentativeCompletedSessions(travelerUserId, nowUtc)
                .Where(session => ids.Contains(session.ItineraryId))
                .Select(session => new
                {
                    session.ItineraryId,
                    Stops = session.Items.Count(),
                    Visited = session.Items.Count(item => item.ReachedAtUtc != null),
                })
                .ToListAsync(cancellationToken);
            sessionCounts = sessions.ToDictionary(session => session.ItineraryId, session => (session.Stops, session.Visited));
        }

        return rows.ToDictionary(
            row => row.Id,
            row => new ItineraryData(
                row.Id,
                row.Title ?? string.Empty,
                row.Status,
                row.PlannedStops,
                sessionCounts.TryGetValue(row.Id, out var counts) ? counts : null));
    }

    private sealed record BookingData(
        long Id,
        string BookingCode,
        int Quantity,
        decimal TotalAmount,
        BookingStatus Status,
        string Title,
        long? TicketId,
        bool HasRefund,
        (long Id, int Rating)? Review)
    {
        public TripHistoryItemResponse ToCard(string tab, TripKey key)
        {
            // P6: only a Completed, not yet reviewed booking offers a review. P7: the e-ticket needs a
            // Confirmed or Completed booking. P8: the refund status needs a Cancelled booking with a refund.
            var canViewETicket = (Status is BookingStatus.Confirmed or BookingStatus.Completed) && TicketId is not null;
            return new TripHistoryItemResponse
            {
                Kind = TripRecordKinds.CommerceBooking,
                Id = Id,
                TripType = ListTravelerTripsQuery.TripTypeTourBooking,
                Tab = tab,
                Title = Title,
                DepartureAtUtc = key.DepartureAtUtc,
                Status = Status.ToString(),
                ReviewableRecord = ReviewableRecordRef.CommerceBooking(Id),
                Actions = new TripActionsResponse(
                    CanReview: Status == BookingStatus.Completed && Review is null,
                    CanViewETicket: canViewETicket,
                    TicketId: canViewETicket ? TicketId : null,
                    CanViewRefund: Status == BookingStatus.Cancelled && HasRefund),
                Review = Review is { } review
                    ? new TripReviewStateResponse(true, review.Id, review.Rating)
                    : new TripReviewStateResponse(false, null, null),
                BookingCode = BookingCode,
                ParticipantCount = Quantity,
                TotalAmount = TotalAmount,
            };
        }
    }

    private sealed record ItineraryData(
        long Id,
        string Title,
        string Status,
        int PlannedStops,
        (int Stops, int Visited)? Session)
    {
        public TripHistoryItemResponse ToCard(string tab, TripKey key)
        {
            var completed = tab == ListTravelerTripsQuery.TabCompleted;
            return new TripHistoryItemResponse
            {
                Kind = TripRecordKinds.Itinerary,
                Id = Id,
                TripType = ListTravelerTripsQuery.TripTypeSelfPlannedItinerary,
                Tab = tab,
                Title = Title,
                DepartureAtUtc = key.DepartureAtUtc,
                Status = completed ? Itinerary.CompletedStatus : Status,
                ReviewableRecord = null,
                Actions = new TripActionsResponse(false, false, null, false),
                Review = null,
                StopCount = completed ? Session?.Stops ?? 0 : PlannedStops,
                VisitedCount = completed ? Session?.Visited ?? 0 : null,
            };
        }
    }
}