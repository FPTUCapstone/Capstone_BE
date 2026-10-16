using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Preferences.Common;

/// <summary>
/// Authoritative UC-09 interest catalogue. Keys are an intentionally small, stable English
/// contract shared by persistence, Web, Mobile, and personalization. They must never be derived
/// from mutable POI categories or tags.
/// </summary>
internal sealed class InterestOptionCatalog
{
    private readonly Dictionary<string, InterestOptionDto> _byToken;

    private InterestOptionCatalog(IReadOnlyList<InterestOptionDto> options)
    {
        Options = options;
        _byToken = options.ToDictionary(
            option => TravelerPreferenceScoring.NormalizePreferenceToken(option.Key),
            StringComparer.Ordinal);
    }

    public IReadOnlyList<InterestOptionDto> Options { get; }

    public static InterestOptionCatalog Fixed { get; } = new(
        TravelerPreferenceKeys.InterestKeys
            .Select(key => new InterestOptionDto(key, TravelerPreferenceKeys.InterestSourceTag))
            .ToArray());

    /// <summary>Returns the canonical key for a submitted or stored value, or null when unknown.</summary>
    public string? Canonicalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return _byToken.TryGetValue(
            TravelerPreferenceScoring.NormalizePreferenceToken(value),
            out var option)
            ? option.Key
            : null;
    }

}

/// <summary>Serialization and mapping helpers shared by the UC-09 handlers.</summary>
internal static class TravelerPreferenceMapping
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        // Keys are stored as written; relaxed escaping keeps non-ASCII tag names compact.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static TravelerPreferencesDto Empty { get; } = new(
        [],
        null,
        null,
        null,
        [],
        null);

    /// <summary>Serializes a key list; an empty list is stored as NULL (spec §5).</summary>
    public static string? SerializeKeys(IReadOnlyCollection<string> keys) =>
        keys.Count == 0 ? null : JsonSerializer.Serialize(keys, CompactJson);

    /// <summary>Parses a stored JSON array; null, blank or malformed JSON yields no values.</summary>
    public static IReadOnlyList<string> ParseKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<string?[]>(json) ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Builds the API representation. Interest and food keys that are no longer offered are
    /// omitted (D-08).
    /// </summary>
    public static TravelerPreferencesDto ToDto(
        TravelerProfile? profile,
        InterestOptionCatalog catalog)
    {
        if (profile is null)
        {
            return Empty;
        }

        var interests = ParseKeys(profile.InterestTagsJson)
            .Select(catalog.Canonicalize)
            .Where(key => key is not null)
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var food = ParseKeys(profile.FoodPreferencesJson)
            .Select(TravelerPreferenceKeys.CanonicalFoodKey)
            .Where(key => key is not null)
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new TravelerPreferencesDto(
            interests,
            profile.PreferredTransportMode?.ToString(),
            profile.TravelPace?.ToString(),
            profile.RiskTolerance?.ToString(),
            food,
            profile.UpdatedAtUtc.ToUniversalTime());
    }
}

/// <summary>Access rule of UC-09: only an Active Traveler may read or write preferences.</summary>
internal static class TravelerPreferenceAccess
{
    public static Task<bool> IsActiveTravelerAsync(
        IApplicationDbContext dbContext,
        long userId,
        CancellationToken cancellationToken) =>
        dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.Id == userId
                    && user.Role == UserRole.Traveler
                    && user.Status == AccountStatus.Active,
                cancellationToken);
}
