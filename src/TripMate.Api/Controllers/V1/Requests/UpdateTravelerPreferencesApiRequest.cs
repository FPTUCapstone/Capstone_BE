namespace TripMate.Api.Controllers.V1.Requests;

/// <summary>
/// UC-09 PUT body. Every field is optional; enum-like values are plain strings so that an unknown
/// key is reported with the UC-09 error code instead of a model-binding error.
/// </summary>
public sealed record UpdateTravelerPreferencesApiRequest(
    IReadOnlyList<string?>? Interests,
    string? PreferredTransportMode,
    string? TravelPace,
    string? RiskTolerance,
    IReadOnlyList<string?>? FoodPreferences);
