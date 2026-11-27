namespace TripMate.Domain.Enums;

/// <summary>
/// Identifies which payment model was used for a booking.
/// PlatformCollectedLegacy: TripMate collected payment on behalf of the TO (pre-cutover model).
/// OperatorDirect: Traveler paid the TO directly via the TO's own payOS channel (new model).
/// </summary>
public enum PaymentFlow
{
    PlatformCollectedLegacy = 0,
    OperatorDirect = 1,
}