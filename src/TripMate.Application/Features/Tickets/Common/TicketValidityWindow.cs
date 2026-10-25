namespace TripMate.Application.Features.Tickets.Common;

/// <summary>
/// UC-29 decision D1: a ticket is accepted during the whole departure date in Asia/Ho_Chi_Minh,
/// 00:00 to 23:59:59.9999999 local. Vietnam has no DST, so a fixed +07:00 offset is used instead of
/// a platform time-zone lookup (zone ids differ between Windows and Linux hosts).
/// </summary>
public readonly record struct TicketValidityWindow(
    DateOnly DepartureDate,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidUntilUtc)
{
    public const string TimeZoneId = "Asia/Ho_Chi_Minh";

    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static TicketValidityWindow For(DateTimeOffset departureStartUtc)
    {
        var departureDate = DateOnly.FromDateTime(departureStartUtc.ToOffset(VietnamOffset).DateTime);
        var localMidnight = new DateTimeOffset(departureDate.ToDateTime(TimeOnly.MinValue), VietnamOffset);
        var validFromUtc = localMidnight.ToUniversalTime();
        return new TicketValidityWindow(departureDate, validFromUtc, validFromUtc.AddDays(1).AddTicks(-1));
    }
}
