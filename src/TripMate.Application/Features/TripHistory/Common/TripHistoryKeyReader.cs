using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripHistory.List;

namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>One merged page of record keys and the total across both streams under the same filters.</summary>
public sealed record TripKeyPage(int TotalCount, IReadOnlyList<TripKey> Keys);

/// <summary>
/// Reads the requested page of keys for UC-32: each included stream is read up to <c>page * pageSize</c>
/// keys (bounded by <see cref="ListTravelerTripsQuery.MaxWindowKeys"/>), merged in
/// <see cref="TripKeyMerger.Order"/> and sliced. No per-record queries happen here; cards are hydrated
/// for the final page only.
/// </summary>
public static class TripHistoryKeyReader
{
    public static async Task<TripKeyPage> ReadPageAsync(
        IApplicationDbContext dbContext,
        ListTravelerTripsQuery query,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var filter = FilterOf(query);
        var windowSize = checked(query.Page * query.PageSize);

        // The streams share one DbContext, so they are read one after the other.
        var bookings = IncludesBookings(query)
            ? await TripBookingStream.ReadAsync(dbContext, filter, windowSize, cancellationToken)
            : TripStreamWindow.Empty;
        var itineraries = IncludesItineraries(query)
            ? await TripItineraryStream.ReadAsync(dbContext, filter, nowUtc, windowSize, cancellationToken)
            : TripStreamWindow.Empty;

        return new TripKeyPage(
            bookings.TotalCount + itineraries.TotalCount,
            TripKeyMerger.Merge(bookings.Keys, itineraries.Keys, query.Page, query.PageSize));
    }

    /// <summary>
    /// P9: Reached items across every Completed self-planned trip matching the query's filters; zero when
    /// the trip-type filter excludes itineraries.
    /// </summary>
    public static Task<int> CountVisitedPoisAsync(
        IApplicationDbContext dbContext,
        ListTravelerTripsQuery query,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        IncludesItineraries(query)
            ? TripItineraryStream.CountVisitedPoisAsync(dbContext, FilterOf(query), nowUtc, cancellationToken)
            : Task.FromResult(0);

    private static bool IncludesBookings(ListTravelerTripsQuery query) =>
        query.TripType is null or ListTravelerTripsQuery.TripTypeAll or ListTravelerTripsQuery.TripTypeTourBooking;

    private static bool IncludesItineraries(ListTravelerTripsQuery query) =>
        query.TripType is null or ListTravelerTripsQuery.TripTypeAll
            or ListTravelerTripsQuery.TripTypeSelfPlannedItinerary;

    private static TripStreamFilter FilterOf(ListTravelerTripsQuery query)
    {
        if (!VietnamDayRange.TryCreate(query.FromDate, query.ToDate, out var range))
        {
            throw new ArgumentException("The date range is invalid.", nameof(query));
        }

        return new TripStreamFilter(
            query.TravelerUserId,
            query.Tab ?? throw new ArgumentException("A tab is required.", nameof(query)),
            range);
    }
}