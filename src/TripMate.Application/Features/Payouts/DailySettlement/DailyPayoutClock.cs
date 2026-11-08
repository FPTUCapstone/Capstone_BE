namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>Vietnam-time helpers shared by the daily payout run and its retry sweep (BR-141:
/// the run happens at 10:00 Vietnam time, every calendar day).</summary>
public static class DailyPayoutClock
{
    public const int RunStartHour = 10;

    /// <summary>End of the same-day retry window (spec decision: 10:00 + 3 hourly retries = 13:00).</summary>
    public const int RetryCutoffHour = 13;

    public static readonly TimeZoneInfo VietnamTimeZone = FindVietnamTimeZone();

    public static DateTimeOffset ToVietnamTime(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, VietnamTimeZone);

    public static DateOnly VietnamDateOf(DateTimeOffset utc) => DateOnly.FromDateTime(ToVietnamTime(utc).Date);

    public static bool IsAtOrAfterRunStart(DateTimeOffset utc) => ToVietnamTime(utc).Hour >= RunStartHour;

    /// <summary>True once Vietnam-local time has passed 13:00 on <paramref name="runDate"/>'s own
    /// day (or any later day) — the retry window belongs to the run that created the batch, not
    /// to whichever day the sweep happens to run on.</summary>
    public static bool IsPastRetryCutoffFor(DateOnly runDate, DateTimeOffset utc)
    {
        var local = ToVietnamTime(utc);
        var localDate = DateOnly.FromDateTime(local.Date);
        if (localDate > runDate) return true;
        return localDate == runDate && local.Hour >= RetryCutoffHour;
    }

    private static TimeZoneInfo FindVietnamTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
    }
}
