using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tickets.Common;

/// <summary>
/// The single place where UC-29 spec §5.2 lives. Rows are evaluated in order: cancellation wins,
/// then a used ticket, then expiry, then the departure window.
/// </summary>
public static class TicketDisplayStatusEvaluator
{
    public static TicketDisplayStatus Evaluate(BookingStatus bookingStatus, TicketStatus ticketStatus,
        TicketValidityWindow window, DateTimeOffset nowUtc)
    {
        if (bookingStatus == BookingStatus.Cancelled || ticketStatus == TicketStatus.Cancelled)
            return TicketDisplayStatus.Cancelled;
        if (ticketStatus == TicketStatus.Used)
            return TicketDisplayStatus.Used;
        if (ticketStatus == TicketStatus.Expired || nowUtc > window.ValidUntilUtc)
            return TicketDisplayStatus.Expired;
        if (nowUtc < window.ValidFromUtc)
            return TicketDisplayStatus.NotYetActive;
        return TicketDisplayStatus.Valid;
    }

    /// <summary>The QR is returned before the departure date too, so it can be shown offline (D5).</summary>
    public static bool ShowsQr(TicketDisplayStatus status) =>
        status is TicketDisplayStatus.NotYetActive or TicketDisplayStatus.Valid;

    public static bool CanRefresh(TicketDisplayStatus status) => ShowsQr(status);
}