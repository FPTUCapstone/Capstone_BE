using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Bookings.Common;

/// <summary>The two ticket columns that decide a booking's check-in state.</summary>
public sealed record TicketCheckInSnapshot(TicketStatus Status, DateTimeOffset? CheckedInAtUtc);

/// <summary>
/// UC-40 check-in derivation. A booking is checked in when any of its tickets is <c>Used</c> or
/// carries a check-in timestamp; the reported timestamp is the earliest non-null one.
/// Evaluated in memory over a lightweight ticket snapshot so it never depends on SQL translation.
/// </summary>
public static class BookingCheckIn
{
    public const string CheckedIn = "CheckedIn";
    public const string NotCheckedIn = "NotCheckedIn";

    public static (string Status, DateTimeOffset? CheckedInAtUtc) Resolve(IEnumerable<TicketCheckInSnapshot> tickets)
    {
        var snapshots = tickets as IReadOnlyCollection<TicketCheckInSnapshot> ?? tickets.ToList();
        bool checkedIn = snapshots.Any(ticket => ticket.Status == TicketStatus.Used || ticket.CheckedInAtUtc != null);
        DateTimeOffset? checkedInAtUtc = snapshots
            .Where(ticket => ticket.CheckedInAtUtc != null)
            .Select(ticket => ticket.CheckedInAtUtc)
            .Min();

        return (checkedIn ? CheckedIn : NotCheckedIn, checkedInAtUtc);
    }
}
