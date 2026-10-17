using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// A visit the traveler wants in the itinerary, in order. <paramref name="TransportModeToNext"/> overrides the
/// trip transport mode for the leg to the next stop; null keeps the trip mode.
/// </summary>
public sealed record ItineraryStop(long PoiId, TransportMode? TransportModeToNext = null);

/// <summary>Why a stop does not fit the itinerary.</summary>
public static class StopInfeasibilityReasons
{
    public const string Inactive = "Inactive";
    public const string NoOpeningHours = "NoOpeningHours";
    public const string OutsideRadius = "OutsideRadius";
    public const string ClosedAtArrival = "ClosedAtArrival";
    public const string ExceedsEndTime = "ExceedsEndTime";
    public const string ExceedsBudget = "ExceedsBudget";
}

/// <summary>The first stop that breaks an ordered itinerary and by how much.</summary>
public sealed record StopInfeasibility(
    long PoiId,
    string Reason,
    DateTimeOffset? PlannedArrival = null,
    TimeOnly? ClosesAt = null,
    int? OverflowMinutes = null,
    decimal? OverflowVnd = null)
{
    public IReadOnlyDictionary<string, object?> ToErrorMetadata() => new Dictionary<string, object?>
    {
        ["poiId"] = PoiId,
        ["reason"] = Reason,
        ["plannedArrival"] = PlannedArrival,
        ["closesAt"] = ClosesAt?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        ["overflowMinutes"] = OverflowMinutes,
        ["overflowVnd"] = OverflowVnd,
    };
}