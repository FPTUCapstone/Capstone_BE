namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>Vietnam-time helpers shared by the daily payout run and its retry sweep (BR-141:
/// the run happens at 10:00 Vietnam time, every calendar day).</summary>
public static class DailyPayoutClock
{
    public const int RunStartHour = 10;

    /// <summary>BR-141 (SRS v12): every paid booking is held this long after its payment
    /// confirmation timestamp before it is eligible for any run, regardless of when the run
    /// itself fires. A cancellation inside the window is refunded from the still-collected
    /// payment (BR-142), never clawed back from a Tour Operator who already received it.</summary>
    public static readonly TimeSpan AwaitingWindowDuration = TimeSpan.FromHours(24);

    public static readonly TimeZoneInfo VietnamTimeZone = FindVietnamTimeZone();

    public static DateTimeOffset ToVietnamTime(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, VietnamTimeZone);

    public static DateOnly VietnamDateOf(DateTimeOffset utc) => DateOnly.FromDateTime(ToVietnamTime(utc).Date);

    public static bool IsAtOrAfterRunStart(DateTimeOffset utc) => ToVietnamTime(utc).Hour >= RunStartHour;

    private static TimeZoneInfo FindVietnamTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
    }
}