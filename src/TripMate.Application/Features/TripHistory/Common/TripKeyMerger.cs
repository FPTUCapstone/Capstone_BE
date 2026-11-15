namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>
/// The total order of Trip History (spec TM-78 §7.1, A5) and the merge of the two streams.
/// Order: departure descending, then kind ordinal ascending (<c>commerceBooking</c> before
/// <c>itinerary</c>), then id descending. Ending on (kind, id) makes it total, so pages never repeat
/// or skip a record.
/// </summary>
public static class TripKeyMerger
{
    public static readonly IComparer<TripKey> Order = Comparer<TripKey>.Create(Compare);

    /// <summary>
    /// Merges two streams that are each already in <see cref="Order"/> and returns the requested page.
    /// To be exact for page N, each stream must supply at least its first <c>N * pageSize</c> keys.
    /// </summary>
    public static IReadOnlyList<TripKey> Merge(
        IReadOnlyList<TripKey> bookings,
        IReadOnlyList<TripKey> itineraries,
        int page,
        int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var skip = (long)(page - 1) * pageSize;
        var result = new List<TripKey>(pageSize);
        long position = 0;
        int b = 0, i = 0;
        while (result.Count < pageSize && (b < bookings.Count || i < itineraries.Count))
        {
            var takeBooking = i >= itineraries.Count
                || (b < bookings.Count && Compare(bookings[b], itineraries[i]) <= 0);
            var next = takeBooking ? bookings[b++] : itineraries[i++];
            if (position++ >= skip)
            {
                result.Add(next);
            }
        }

        return result;
    }

    private static int Compare(TripKey left, TripKey right)
    {
        var byDeparture = right.DepartureAtUtc.CompareTo(left.DepartureAtUtc);
        if (byDeparture != 0)
        {
            return byDeparture;
        }

        var byKind = string.CompareOrdinal(left.Kind, right.Kind);
        return byKind != 0 ? byKind : right.Id.CompareTo(left.Id);
    }
}