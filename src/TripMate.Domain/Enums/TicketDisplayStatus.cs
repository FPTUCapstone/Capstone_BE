namespace TripMate.Domain.Enums;

/// <summary>
/// UC-29 status shown to the Traveler. Derived from the booking, the stored ticket status and the
/// departure window at read time; never persisted (the SQL check has no NotYetActive).
/// </summary>
public enum TicketDisplayStatus
{
    NotYetActive,
    Valid,
    Used,
    Cancelled,
    Expired,
}