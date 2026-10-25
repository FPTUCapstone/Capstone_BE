namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the commerce.TicketQrCodes.status SQL check constraint.
/// </summary>
public enum TicketQrCodeStatus
{
    Active,
    Consumed,
    Revoked,
    Expired,
}
