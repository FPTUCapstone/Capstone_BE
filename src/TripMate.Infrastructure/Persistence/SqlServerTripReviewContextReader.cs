using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Persistence;

/// <summary>
/// Advisory owner read only. Create/edit recheck the same typed identity inside
/// their final transaction; planned stops are never promoted to visit evidence.
/// </summary>
public sealed class SqlServerTripReviewContextReader(ApplicationDbContext dbContext) : ITripReviewContextReader
{
    public Task<TripReviewContextData?> ReadOwnedAsync(long bookingId, long travelerUserId,
        CancellationToken cancellationToken) => bookingId > 0
        ? ReadOwnedAsync(ReviewableRecordRef.CommerceBooking(bookingId), travelerUserId, cancellationToken)
        : Task.FromResult<TripReviewContextData?>(null);

    public Task<TripReviewContextData?> ReadOwnedForUpdateAsync(long bookingId, long travelerUserId,
        CancellationToken cancellationToken) => bookingId > 0
        ? ReadOwnedForUpdateAsync(ReviewableRecordRef.CommerceBooking(bookingId), travelerUserId, cancellationToken)
        : Task.FromResult<TripReviewContextData?>(null);

    public Task<TripReviewContextData?> ReadOwnedAsync(ReviewableRecordRef reviewableRecord,
        long travelerUserId, CancellationToken cancellationToken) =>
        ReadAsync(reviewableRecord, travelerUserId, forUpdate: false, cancellationToken);

    public Task<TripReviewContextData?> ReadOwnedForUpdateAsync(ReviewableRecordRef reviewableRecord,
        long travelerUserId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A locked trip-review context read requires a caller-owned transaction.");
        return ReadAsync(reviewableRecord, travelerUserId, forUpdate: true, cancellationToken);
    }

    private async Task<TripReviewContextData?> ReadAsync(ReviewableRecordRef reviewableRecord,
        long travelerUserId, bool forUpdate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        if (travelerUserId <= 0) return null;

        var row = reviewableRecord.IsCommerceBooking
            ? await ReadCommerceAsync(reviewableRecord.Id, travelerUserId, forUpdate, cancellationToken)
            : await ReadServiceAsync(reviewableRecord.Id, travelerUserId, forUpdate, cancellationToken);
        if (row is null) return null;

        var booking = new TripReviewBookingData
        {
            BookingId = row.RecordId,
            ReviewableRecord = reviewableRecord,
            BookingStatus = row.BookingStatus,
            TourScheduleId = row.TourScheduleId,
            TourId = row.TourId,
            ItineraryId = row.ItineraryId,
            ItineraryOwnerId = row.ItineraryOwnerId,
            ItinerarySourceType = row.ItinerarySourceType,
            SourceTourId = row.SourceTourId,
            SchedulingRequestId = row.SchedulingRequestId,
            SchedulingRequestOwnerId = row.SchedulingRequestOwnerId,
            PoiId = row.PoiId,
            SummaryName = row.SummaryName,
            DepartureAtUtc = new DateTimeOffset(DateTime.SpecifyKind(row.DepartureAtUtc, DateTimeKind.Utc)),
            BookingReference = row.BookingReference,
            TravelerRole = row.TravelerRole,
            TravelerStatus = row.TravelerStatus,
            TravelerFullName = row.TravelerFullName,
        };

        var parents = reviewableRecord.IsCommerceBooking
            ? await dbContext.TripReviews.AsNoTracking()
                .Where(review => review.BookingId == reviewableRecord.Id && review.ServiceBookingId == null)
                .ToListAsync(cancellationToken)
            : await dbContext.TripReviews.AsNoTracking()
                .Where(review => review.ServiceBookingId == reviewableRecord.Id && review.BookingId == null)
                .ToListAsync(cancellationToken);
        var parent = parents.SingleOrDefault(review => review.TravelerUserId == travelerUserId);
        var foreignParent = parents.Any(review => review.TravelerUserId != travelerUserId);

        if (reviewableRecord.IsServiceBooking)
            return new(booking, parent, [], false, foreignParent);

        // Legacy rows only carry commerce.Bookings.booking_id and cannot cross
        // namespaces when a ServiceBooking happens to use the same number.
        var legacyRows = await dbContext.Reviews.AsNoTracking()
            .Where(review => review.BookingId == reviewableRecord.Id).ToListAsync(cancellationToken);
        var ownLegacy = legacyRows.Where(review => review.TravelerUserId == travelerUserId)
            .OrderBy(review => review.Id)
            .Select(review => new LegacyTripReviewEntryDto(review.Id, review.TargetType, review.TargetId,
                review.Rating, review.Comment, review.CreatedAtUtc)).ToList();
        return new(booking, parent, ownLegacy,
            legacyRows.Any(review => review.TravelerUserId != travelerUserId), foreignParent);
    }

    private Task<TripReviewContextRow?> ReadCommerceAsync(long bookingId, long travelerUserId,
        bool forUpdate, CancellationToken cancellationToken) => forUpdate
        ? dbContext.Database.SqlQuery<TripReviewContextRow>($"""
            SELECT b.booking_id AS RecordId, b.status AS BookingStatus,
                b.tour_schedule_id AS TourScheduleId, t.tour_id AS TourId,
                b.itinerary_id AS ItineraryId, i.traveler_user_id AS ItineraryOwnerId,
                i.source_type AS ItinerarySourceType, i.source_tour_id AS SourceTourId,
                i.scheduling_request_id AS SchedulingRequestId, r.traveler_user_id AS SchedulingRequestOwnerId,
                CAST(NULL AS BIGINT) AS PoiId,
                COALESCE(NULLIF(TRIM(t.title),N''),NULLIF(TRIM(i.title),N''),CONCAT(N'Booking ',b.booking_code)) AS SummaryName,
                COALESCE(s.start_datetime,i.valid_from,b.booked_at) AS DepartureAtUtc,
                CAST(b.booking_code AS NVARCHAR(150)) AS BookingReference,
                u.role AS TravelerRole, u.status AS TravelerStatus, u.full_name AS TravelerFullName
            FROM commerce.Bookings AS b WITH (HOLDLOCK)
            JOIN dbo.Users AS u WITH (HOLDLOCK) ON u.user_id=b.traveler_user_id
            LEFT JOIN commerce.TourSchedules AS s WITH (HOLDLOCK) ON s.schedule_id=b.tour_schedule_id
            LEFT JOIN commerce.Tours AS t WITH (HOLDLOCK) ON t.tour_id=s.tour_id
            LEFT JOIN planning.Itineraries AS i WITH (HOLDLOCK) ON i.itinerary_id=b.itinerary_id
            LEFT JOIN planning.SchedulingRequests AS r WITH (HOLDLOCK) ON r.request_id=i.scheduling_request_id
            WHERE b.booking_id={bookingId} AND b.traveler_user_id={travelerUserId}
            """).SingleOrDefaultAsync(cancellationToken)
        : dbContext.Database.SqlQuery<TripReviewContextRow>($"""
            SELECT b.booking_id AS RecordId, b.status AS BookingStatus,
                b.tour_schedule_id AS TourScheduleId, t.tour_id AS TourId,
                b.itinerary_id AS ItineraryId, i.traveler_user_id AS ItineraryOwnerId,
                i.source_type AS ItinerarySourceType, i.source_tour_id AS SourceTourId,
                i.scheduling_request_id AS SchedulingRequestId, r.traveler_user_id AS SchedulingRequestOwnerId,
                CAST(NULL AS BIGINT) AS PoiId,
                COALESCE(NULLIF(TRIM(t.title),N''),NULLIF(TRIM(i.title),N''),CONCAT(N'Booking ',b.booking_code)) AS SummaryName,
                COALESCE(s.start_datetime,i.valid_from,b.booked_at) AS DepartureAtUtc,
                CAST(b.booking_code AS NVARCHAR(150)) AS BookingReference,
                u.role AS TravelerRole, u.status AS TravelerStatus, u.full_name AS TravelerFullName
            FROM commerce.Bookings AS b
            JOIN dbo.Users AS u ON u.user_id=b.traveler_user_id
            LEFT JOIN commerce.TourSchedules AS s ON s.schedule_id=b.tour_schedule_id
            LEFT JOIN commerce.Tours AS t ON t.tour_id=s.tour_id
            LEFT JOIN planning.Itineraries AS i ON i.itinerary_id=b.itinerary_id
            LEFT JOIN planning.SchedulingRequests AS r ON r.request_id=i.scheduling_request_id
            WHERE b.booking_id={bookingId} AND b.traveler_user_id={travelerUserId}
                AND u.role={nameof(UserRole.Traveler)} AND u.status={nameof(AccountStatus.Active)}
            """).SingleOrDefaultAsync(cancellationToken);

    private Task<TripReviewContextRow?> ReadServiceAsync(long serviceBookingId, long travelerUserId,
        bool forUpdate, CancellationToken cancellationToken) => forUpdate
        ? dbContext.Database.SqlQuery<TripReviewContextRow>($"""
            SELECT b.service_booking_id AS RecordId, b.status AS BookingStatus,
                CAST(NULL AS BIGINT) AS TourScheduleId, CAST(NULL AS BIGINT) AS TourId,
                CAST(NULL AS BIGINT) AS ItineraryId, CAST(NULL AS BIGINT) AS ItineraryOwnerId,
                CAST(NULL AS VARCHAR(14)) AS ItinerarySourceType, CAST(NULL AS BIGINT) AS SourceTourId,
                CAST(NULL AS BIGINT) AS SchedulingRequestId, CAST(NULL AS BIGINT) AS SchedulingRequestOwnerId,
                s.poi_id AS PoiId, s.name AS SummaryName, b.start_datetime AS DepartureAtUtc,
                COALESCE(NULLIF(TRIM(b.provider_reference),N''),CONCAT(N'SB-',b.service_booking_id)) AS BookingReference,
                u.role AS TravelerRole, u.status AS TravelerStatus, u.full_name AS TravelerFullName
            FROM commercial.ServiceBookings AS b WITH (HOLDLOCK)
            JOIN commercial.Services AS s WITH (HOLDLOCK) ON s.service_id=b.service_id
            JOIN dbo.Users AS u WITH (HOLDLOCK) ON u.user_id=b.traveler_user_id
            WHERE b.service_booking_id={serviceBookingId} AND b.traveler_user_id={travelerUserId}
            """).SingleOrDefaultAsync(cancellationToken)
        : dbContext.Database.SqlQuery<TripReviewContextRow>($"""
            SELECT b.service_booking_id AS RecordId, b.status AS BookingStatus,
                CAST(NULL AS BIGINT) AS TourScheduleId, CAST(NULL AS BIGINT) AS TourId,
                CAST(NULL AS BIGINT) AS ItineraryId, CAST(NULL AS BIGINT) AS ItineraryOwnerId,
                CAST(NULL AS VARCHAR(14)) AS ItinerarySourceType, CAST(NULL AS BIGINT) AS SourceTourId,
                CAST(NULL AS BIGINT) AS SchedulingRequestId, CAST(NULL AS BIGINT) AS SchedulingRequestOwnerId,
                s.poi_id AS PoiId, s.name AS SummaryName, b.start_datetime AS DepartureAtUtc,
                COALESCE(NULLIF(TRIM(b.provider_reference),N''),CONCAT(N'SB-',b.service_booking_id)) AS BookingReference,
                u.role AS TravelerRole, u.status AS TravelerStatus, u.full_name AS TravelerFullName
            FROM commercial.ServiceBookings AS b
            JOIN commercial.Services AS s ON s.service_id=b.service_id
            JOIN dbo.Users AS u ON u.user_id=b.traveler_user_id
            WHERE b.service_booking_id={serviceBookingId} AND b.traveler_user_id={travelerUserId}
                AND u.role={nameof(UserRole.Traveler)} AND u.status={nameof(AccountStatus.Active)}
            """).SingleOrDefaultAsync(cancellationToken);

    private sealed class TripReviewContextRow
    {
        public long RecordId { get; init; }
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
        public DateTime DepartureAtUtc { get; init; }
        public string BookingReference { get; init; } = string.Empty;
        public string TravelerRole { get; init; } = string.Empty;
        public string TravelerStatus { get; init; } = string.Empty;
        public string? TravelerFullName { get; init; }
    }
}