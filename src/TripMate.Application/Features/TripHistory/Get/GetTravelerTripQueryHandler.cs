using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripHistory.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripHistory.Get;

/// <summary>
/// UC-32 detail: one record the caller owns, as the same card the list shows plus the kind-specific
/// detail. A record that does not exist, belongs to someone else or is not a trip returns the same
/// not-found result, so existence is never leaked. Read-only (PC-03).
/// </summary>
public sealed class GetTravelerTripQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock)
    : IRequestHandler<GetTravelerTripQuery, Result<TripDetailResponse>>
{
    private static readonly string[] InScopeSources = [Itinerary.ManualSourceType, Itinerary.CspGeneratedSourceType];

    public async Task<Result<TripDetailResponse>> Handle(
        GetTravelerTripQuery request,
        CancellationToken cancellationToken)
    {
        var detail = request.Kind switch
        {
            TripRecordKinds.CommerceBooking => await GetBookingAsync(request, cancellationToken),
            TripRecordKinds.Itinerary => await GetItineraryAsync(request, cancellationToken),
            _ => null,
        };

        return detail is null
            ? Result.Failure<TripDetailResponse>(
                TripHistoryErrorCodes.RecordNotFound,
                "The trip record was not found.")
            : Result.Success(detail);
    }

    private async Task<TripDetailResponse?> GetBookingAsync(GetTravelerTripQuery request, CancellationToken cancellationToken)
    {
        var travelerId = request.TravelerUserId;
        var bookingId = request.Id;
        var row = await dbContext.Bookings.AsNoTracking()
            .Where(booking => booking.Id == bookingId && booking.TravelerUserId == travelerId)
            .Select(booking => new
            {
                booking.Status,
                booking.CancelledAtUtc,
                booking.CancelReason,
                StartAtUtc = booking.TourSchedule.StartAtUtc,
                EndAtUtc = booking.TourSchedule.EndAtUtc,
                booking.TourSchedule.MeetingPoint,
                TourTitle = booking.TourSchedule.Tour.Title,
                booking.TourSchedule.Tour.DestinationSummary,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var card = await HydrateAsync(
            TripHistoryClassifier.TabForBooking(row.Status),
            new TripKey(TripRecordKinds.CommerceBooking, bookingId, row.StartAtUtc),
            travelerId,
            cancellationToken);
        if (card is null)
        {
            return null;
        }

        var counts = await dbContext.BookingParticipants.AsNoTracking()
            .Where(participant => participant.BookingId == bookingId)
            .GroupBy(participant => participant.ParticipantType)
            .Select(group => new { Type = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        int CountOf(ParticipantType type) => counts.FirstOrDefault(count => count.Type == type)?.Count ?? 0;

        var cancelled = row.Status == BookingStatus.Cancelled;
        return new TripDetailResponse(card)
        {
            Tour = new TripTourInfo(row.TourTitle, row.DestinationSummary),
            Schedule = new TripScheduleInfo(row.StartAtUtc, row.EndAtUtc, row.MeetingPoint),
            Participants = new TripParticipantCounts(
                CountOf(ParticipantType.Adult), CountOf(ParticipantType.Child), CountOf(ParticipantType.Infant)),
            Cancellation = cancelled ? new TripCancellationInfo(row.CancelledAtUtc, row.CancelReason) : null,
            Refund = cancelled ? await GetLatestRefundAsync(bookingId, cancellationToken) : null,
        };
    }

    /// <summary>T3: the refund with the highest id; its channel is the original payment's gateway.</summary>
    private async Task<TripRefundInfo?> GetLatestRefundAsync(long bookingId, CancellationToken cancellationToken)
    {
        var refund = await dbContext.Refunds.AsNoTracking()
            .Where(candidate => candidate.BookingId == bookingId)
            .OrderByDescending(candidate => candidate.Id)
            .Select(candidate => new { candidate.Status, candidate.Amount, candidate.TransactionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (refund is null)
        {
            return null;
        }

        PaymentGateway? gateway = null;
        if (refund.TransactionId is { } transactionId)
        {
            gateway = await dbContext.PaymentTransactions.AsNoTracking()
                .Where(payment => payment.Id == transactionId && payment.BookingId == bookingId)
                .Select(payment => (PaymentGateway?)payment.Gateway)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new TripRefundInfo(refund.Status.ToString(), refund.Amount, gateway?.ToString());
    }

    private async Task<TripDetailResponse?> GetItineraryAsync(GetTravelerTripQuery request, CancellationToken cancellationToken)
    {
        var travelerId = request.TravelerUserId;
        var itineraryId = request.Id;
        var nowUtc = clock.UtcNow;
        var itinerary = await dbContext.Itineraries.AsNoTracking()
            .Where(candidate => candidate.Id == itineraryId
                && candidate.TravelerUserId == travelerId
                && InScopeSources.Contains(candidate.SourceType))
            .Select(candidate => new
            {
                candidate.Status,
                candidate.ValidFromUtc,
                candidate.ValidToUtc,
                candidate.CreatedAtUtc,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (itinerary is null)
        {
            return null;
        }

        // T1 / T4: the same representative session the list and the summary use.
        var session = await dbContext.TripSessions.AsNoTracking()
            .RepresentativeCompletedSessions(travelerId, nowUtc)
            .Where(candidate => candidate.ItineraryId == itineraryId)
            .Include(candidate => candidate.Items)
            .FirstOrDefaultAsync(cancellationToken);
        var tab = TripHistoryClassifier.TabForItinerary(itinerary.Status, hasCompletedSession: session is not null);
        if (tab is null)
        {
            return null;
        }

        var departure = TripHistoryClassifier.DepartureForItinerary(
            session?.StartedAtUtc, itinerary.ValidFromUtc, itinerary.CreatedAtUtc);
        var card = await HydrateAsync(
            tab,
            new TripKey(TripRecordKinds.Itinerary, itineraryId, departure),
            travelerId,
            cancellationToken);
        if (card is null)
        {
            return null;
        }

        var stops = session is not null
            ? session.Items.OrderBy(item => item.SequenceNo)
                .Select(item => new TripStopResponse(
                    item.SequenceNo,
                    item.PoiId,
                    item.PoiName,
                    item.PlannedArrivalUtc,
                    item.PlannedDepartureUtc,
                    item.Status,
                    item.ReachedAtUtc,
                    item.SkippedAtUtc))
                .ToArray()
            : await GetPlannedStopsAsync(itineraryId, cancellationToken);

        return new TripDetailResponse(card)
        {
            ValidFromUtc = itinerary.ValidFromUtc,
            ValidToUtc = itinerary.ValidToUtc,
            Stops = stops,
            Session = session is null
                ? null
                : new TripSessionInfo(
                    session.StartedAtUtc ?? departure,
                    session.EffectiveEndedAtUtc(nowUtc),
                    session.EffectiveCompletionReason(nowUtc)),
        };
    }

    private async Task<TripStopResponse[]> GetPlannedStopsAsync(long itineraryId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Itineraries.AsNoTracking()
            .Where(candidate => candidate.Id == itineraryId)
            .SelectMany(candidate => candidate.Items)
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.PointOfInterestId != null)
            .OrderBy(item => item.SequenceNo)
            .Select(item => new
            {
                item.SequenceNo,
                PoiId = item.PointOfInterestId!.Value,
                PoiName = item.PointOfInterest!.Name,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new TripStopResponse(
                row.SequenceNo, row.PoiId, row.PoiName, row.PlannedArrivalUtc, row.PlannedDepartureUtc, null, null, null))
            .ToArray();
    }

    private async Task<TripHistoryItemResponse?> HydrateAsync(
        string tab,
        TripKey key,
        long travelerId,
        CancellationToken cancellationToken)
    {
        var cards = await TripCardHydrator.HydrateAsync(
            dbContext, travelerId, tab, [key], clock.UtcNow, cancellationToken);
        return cards.Count == 0 ? null : cards[0];
    }
}