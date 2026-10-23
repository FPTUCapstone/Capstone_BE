using System.Globalization;

namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>
/// An inclusive range of Vietnam calendar days (CR-07) as UTC instants: <c>[FromUtc, ToUtcExclusive)</c>.
/// Vietnam has no daylight saving, so a fixed +07:00 offset is used instead of a platform time-zone lookup.
/// </summary>
public readonly record struct VietnamDayRange(DateTimeOffset? FromUtc, DateTimeOffset? ToUtcExclusive)
{
    public const string DateFormat = "yyyy-MM-dd";

    private static readonly TimeSpan VietnamUtcOffset = TimeSpan.FromHours(7);

    /// <summary>
    /// Parses optional <c>yyyy-MM-dd</c> bounds. Returns false for a malformed or unrepresentable
    /// date and when <paramref name="fromDate"/> is after <paramref name="toDate"/>.
    /// </summary>
    public static bool TryCreate(string? fromDate, string? toDate, out VietnamDayRange range)
    {
        range = default;
        DateTimeOffset? from = null;
        DateTimeOffset? toExclusive = null;
        DateOnly fromDay = default;
        DateOnly toDay = default;

        if (fromDate is not null)
        {
            if (!TryParseDay(fromDate, out fromDay, out var start, out _))
            {
                return false;
            }

            from = start;
        }

        if (toDate is not null)
        {
            if (!TryParseDay(toDate, out toDay, out _, out var nextStart))
            {
                return false;
            }

            toExclusive = nextStart;
        }

        if (fromDate is not null && toDate is not null && fromDay > toDay)
        {
            return false;
        }

        range = new VietnamDayRange(from, toExclusive);
        return true;
    }

    public bool Contains(DateTimeOffset instant) =>
        (FromUtc is null || instant >= FromUtc) && (ToUtcExclusive is null || instant < ToUtcExclusive);

    private static bool TryParseDay(string value, out DateOnly day, out DateTimeOffset startUtc, out DateTimeOffset nextStartUtc)
    {
        startUtc = default;
        nextStartUtc = default;
        if (!DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
        {
            return false;
        }

        try
        {
            startUtc = StartOfDayUtc(day);
            nextStartUtc = StartOfDayUtc(day.AddDays(1));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static DateTimeOffset StartOfDayUtc(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), VietnamUtcOffset).ToUniversalTime();
}