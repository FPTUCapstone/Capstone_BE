using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// UC-29 e-ticket for a confirmed booking, stored in commerce.Tickets. One ticket per booking
/// (participant_id stays NULL). The QR payload lives in <see cref="TicketQrCode"/> so it can be
/// rotated without touching the ticket. The departure window is derived from the schedule at
/// read time, so <see cref="ExpiresAtUtc"/> is not written by UC-29.
/// </summary>
public sealed class Ticket : BaseEntity
{
    public const int TicketCodeMaxLength = 30;
    public const string TicketCodePrefix = "TK-";

    private Ticket()
    {
    }

    public long BookingId { get; private set; }

    public Booking Booking { get; private set; } = null!;

    public long? ParticipantId { get; private set; }

    public string TicketCode { get; private set; } = string.Empty;

    public TicketStatus Status { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public DateTimeOffset? CheckedInAtUtc { get; private set; }

    /// <summary>TK- plus the booking id padded to ten digits; the backfill script uses the same rule.</summary>
    public static string CodeFor(long bookingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bookingId);
        return $"{TicketCodePrefix}{bookingId:D10}";
    }

    internal static Ticket IssueFor(Booking booking, DateTimeOffset issuedAtUtc) => new()
    {
        BookingId = booking.Id,
        Booking = booking,
        TicketCode = CodeFor(booking.Id),
        Status = TicketStatus.Valid,
        IssuedAtUtc = issuedAtUtc.ToUniversalTime(),
    };

    public void Cancel()
    {
        if (Status == TicketStatus.Used)
        {
            throw new InvalidOperationException("A used ticket cannot be cancelled.");
        }

        if (Status == TicketStatus.Valid)
        {
            Status = TicketStatus.Cancelled;
        }
    }
}