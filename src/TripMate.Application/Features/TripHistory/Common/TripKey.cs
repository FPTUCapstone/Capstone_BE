namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>The sortable identity of one Trip History record: enough to merge and page the streams.</summary>
public readonly record struct TripKey(string Kind, long Id, DateTimeOffset DepartureAtUtc);

/// <summary>What one stream applies: the owner, the tab and the Vietnam-day date range.</summary>
public readonly record struct TripStreamFilter(long TravelerUserId, string Tab, VietnamDayRange Range);

/// <summary>
/// The newest <c>windowSize</c> keys of a stream in (departure desc, id desc) order plus the stream's
/// full count under the same filter, so a merged page and its total never disagree.
/// </summary>
public sealed record TripStreamWindow(int TotalCount, IReadOnlyList<TripKey> Keys)
{
    public static readonly TripStreamWindow Empty = new(0, []);
}