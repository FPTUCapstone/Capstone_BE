namespace TripMate.Application.Features.Admin.ActiveTrips.GetList;

internal static class VietnamCalendar
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public static DateTimeOffset StartUtc(DateOnly date)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone), TimeSpan.Zero);
    }

    public static DateOnly Today(DateTimeOffset nowUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, Zone).DateTime);

    public static int CurrentDay(DateTimeOffset startedAtUtc, DateTimeOffset nowUtc)
    {
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startedAtUtc, Zone).DateTime);
        var today = Today(nowUtc);
        return Math.Max(1, today.DayNumber - start.DayNumber + 1);
    }
}