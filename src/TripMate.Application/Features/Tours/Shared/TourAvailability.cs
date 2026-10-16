namespace TripMate.Application.Features.Tours.Shared;

/// <summary>
/// Classifies a departure's capacity snapshot. The vocabulary and rules are the ones UC-24 search
/// already publishes, so the list and the detail page never disagree. A snapshot is not a
/// reservation (BR-59).
/// </summary>
public static class TourAvailability
{
    public const string Available = "available";
    public const string SoldOut = "soldOut";
    public const string Unknown = "unknown";

    public static bool IsValidSnapshot(int totalCapacity, int reservedCapacity) =>
        totalCapacity > 0 && reservedCapacity >= 0 && reservedCapacity <= totalCapacity;

    public static string Classify(int totalCapacity, int reservedCapacity)
    {
        if (!IsValidSnapshot(totalCapacity, reservedCapacity)) return Unknown;
        return reservedCapacity < totalCapacity ? Available : SoldOut;
    }

    /// <summary>Null when the snapshot is invalid, so corrupt data is never shown as a number.</summary>
    public static int? RemainingSlots(int totalCapacity, int reservedCapacity) =>
        IsValidSnapshot(totalCapacity, reservedCapacity) ? totalCapacity - reservedCapacity : null;
}