namespace TripMate.Application.Features.Traveler.Preferences.Common;

public sealed record TravelerPreferencesDto(
    IReadOnlyList<string> Interests,
    string? PreferredTransportMode,
    string? TravelPace,
    string? RiskTolerance,
    IReadOnlyList<string> FoodPreferences,
    DateTimeOffset? UpdatedAtUtc);

public sealed record InterestOptionDto(string Key, string Source);

public sealed record TravelerPreferenceOptionsDto(
    IReadOnlyList<InterestOptionDto> Interests,
    IReadOnlyList<string> PreferredTransportModes,
    IReadOnlyList<string> TravelPaces,
    IReadOnlyList<string> FoodPreferences,
    IReadOnlyList<string> RiskTolerances);
