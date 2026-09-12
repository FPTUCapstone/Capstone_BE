using TripMate.Domain.Entities;

namespace TripMate.Application.Features.PointsOfInterest.Common;

/// <summary>
/// Pure calculator for Vietnam-time opening state.
/// Uses fixed UTC+07:00 offset (Asia/Ho_Chi_Minh).
/// Sunday = day 0. Open when: openTime &lt;= localTime &lt; closeTime.
/// Missing day or isClosed=true → closed.
/// </summary>
public static class PoiOpeningState
{
    private static readonly TimeSpan VietnamUtcOffset = TimeSpan.FromHours(7);

    /// <summary>
    /// Determines whether a POI is currently open based on its opening hours
    /// and the current UTC time converted to Vietnam time (UTC+07:00).
    /// </summary>
    public static bool IsOpenNow(
        IEnumerable<PoiOpeningHour> openingHours,
        DateTimeOffset utcNow)
    {
        var (vietnamDay, vietnamTime) = GetVietnamDayAndTime(utcNow);

        foreach (var hours in openingHours)
        {
            if (hours.DayOfWeek != vietnamDay)
            {
                continue;
            }

            if (hours.IsClosed)
            {
                return false;
            }

            return hours.OpenTime.HasValue
                && hours.CloseTime.HasValue
                && hours.OpenTime.Value <= vietnamTime
                && vietnamTime < hours.CloseTime.Value;
        }

        // No entry for this day → closed
        return false;
    }

    /// <summary>
    /// Converts a UTC timestamp to Vietnam local day-of-week (Sunday=0)
    /// and time-of-day. The returned day uses the POI schema convention:
    /// 0=Sunday, 1=Monday, ..., 6=Saturday.
    /// </summary>
    public static (byte Day, TimeOnly Time) GetVietnamDayAndTime(DateTimeOffset utcNow)
    {
        var vietnamDateTime = utcNow.ToOffset(VietnamUtcOffset);
        var vietnamDay = vietnamDateTime.DayOfWeek switch
        {
            System.DayOfWeek.Sunday => (byte)0,
            System.DayOfWeek.Monday => (byte)1,
            System.DayOfWeek.Tuesday => (byte)2,
            System.DayOfWeek.Wednesday => (byte)3,
            System.DayOfWeek.Thursday => (byte)4,
            System.DayOfWeek.Friday => (byte)5,
            System.DayOfWeek.Saturday => (byte)6,
            _ => throw new ArgumentOutOfRangeException(),
        };
        var vietnamTime = TimeOnly.FromTimeSpan(vietnamDateTime.TimeOfDay);

        return (vietnamDay, vietnamTime);
    }
}