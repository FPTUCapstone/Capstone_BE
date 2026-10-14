using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripReviews.Common;

public interface ITripReviewContextReader
{
    Task<TripReviewContextData?> ReadOwnedAsync(long bookingId, long travelerUserId, CancellationToken cancellationToken);
    Task<TripReviewContextData?> ReadOwnedForUpdateAsync(long bookingId, long travelerUserId,
        CancellationToken cancellationToken);

    Task<TripReviewContextData?> ReadOwnedAsync(ReviewableRecordRef reviewableRecord, long travelerUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        return reviewableRecord.IsCommerceBooking
            ? ReadOwnedAsync(reviewableRecord.Id, travelerUserId, cancellationToken)
            : Task.FromException<TripReviewContextData?>(
                new NotSupportedException("This context reader does not support service bookings."));
    }

    Task<TripReviewContextData?> ReadOwnedForUpdateAsync(ReviewableRecordRef reviewableRecord,
        long travelerUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        return reviewableRecord.IsCommerceBooking
            ? ReadOwnedForUpdateAsync(reviewableRecord.Id, travelerUserId, cancellationToken)
            : Task.FromException<TripReviewContextData?>(
                new NotSupportedException("This context reader does not support service bookings."));
    }
}

/// <summary>Internal read evidence, not a wire DTO or historical route/visit proof.</summary>
public sealed record TripReviewBookingData
{
    private ReviewableRecordRef? _reviewableRecord;

    public long BookingId { get; init; }
    public ReviewableRecordRef ReviewableRecord
    {
        get => _reviewableRecord ?? ReviewableRecordRef.CommerceBooking(BookingId);
        init => _reviewableRecord = value;
    }
    public string BookingStatus { get; init; } = string.Empty;
    public long? TourScheduleId { get; init; }
    public long? TourId { get; init; }
    public long? ItineraryId { get; init; }
    public long? ItineraryOwnerId { get; init; }
    public string? ItinerarySourceType { get; init; }
    public long? SourceTourId { get; init; }
    public long? SchedulingRequestId { get; init; }
    public long? SchedulingRequestOwnerId { get; init; }
    public long? PoiId { get; init; }
    public string SummaryName { get; init; } = string.Empty;
    public DateTimeOffset DepartureAtUtc { get; init; }
    public string BookingReference { get; init; } = string.Empty;
    public string TravelerRole { get; init; } = string.Empty;
    public string TravelerStatus { get; init; } = string.Empty;
    public string? TravelerFullName { get; init; }
}

public sealed record TripReviewContextData(TripReviewBookingData Booking, TripReview? Parent,
    IReadOnlyList<LegacyTripReviewEntryDto> OwnedLegacyEntries, bool HasForeignLegacyAuthor, bool HasForeignParent);