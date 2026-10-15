using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Preferences.Common;

namespace TripMate.Application.Features.Traveler.Preferences.Update;

/// <summary>
/// UC-09: replaces all five preference dimensions (spec §6.3). Null or omitted single values
/// become unspecified; null or omitted collections become empty.
/// </summary>
public sealed record UpdateTravelerPreferencesCommand(
    long TravelerUserId,
    IReadOnlyList<string?>? Interests,
    string? PreferredTransportMode,
    string? TravelPace,
    string? RiskTolerance,
    IReadOnlyList<string?>? FoodPreferences)
    : IRequest<Result<TravelerPreferencesDto>>;
