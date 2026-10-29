namespace TripMate.Application.Features.Tickets.Common;

/// <summary>UC-29 error codes; HTTP mapping in ApiControllerBase, SRS messages in docs/UC-29-api-contract.md.</summary>
public static class TicketErrorCodes
{
    public const string Unauthorized = "ticket.unauthorized";
    public const string Forbidden = "ticket.forbidden";
    public const string NotFound = "ticket.not_found";
    public const string BookingNotConfirmed = "ticket.booking_not_confirmed";
    public const string NotIssued = "ticket.not_issued";
    public const string NotRefreshable = "ticket.not_refreshable";

    /// <summary>Problem-details extension carrying the current status on <see cref="NotRefreshable"/>.</summary>
    public const string TicketStatusMetadataKey = "ticketStatus";
}