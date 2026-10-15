using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Preferences.Common;

/// <summary>
/// Interest option catalogue of UC-09 (spec §3.1, D-01, D-07). Interest keys are the canonical
/// names of POI categories and tags, de-duplicated with the same normalization that
/// <see cref="TravelerPreferenceScoring"/> uses, so every offered key can match during ranking.
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

    public static async Task<InterestOptionCatalog> LoadAsync(
        IApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var categoryNames = await dbContext.PoiCategories
            .AsNoTracking()
            .Select(category => category.Name)
            .ToListAsync(cancellationToken);
        var tagNames = await dbContext.Tags
            .AsNoTracking()
            .Select(tag => tag.Name)
            .ToListAsync(cancellationToken);

        return Build(categoryNames, tagNames);
    }

    internal static InterestOptionCatalog Build(
        IEnumerable<string> categoryNames,
        IEnumerable<string> tagNames)
    {
        var excluded = TravelerPreferenceKeys.ExcludedInterestCategories
            .Select(TravelerPreferenceScoring.NormalizePreferenceToken)
            .ToHashSet(StringComparer.Ordinal);
        var byToken = new Dictionary<string, InterestOptionDto>(StringComparer.Ordinal);

        // Categories first so that a category spelling wins over a tag with the same token.
        foreach (var name in categoryNames)
        {
            Add(byToken, excluded, name, TravelerPreferenceKeys.InterestSourceCategory);
        }

        foreach (var name in tagNames)
        {
            Add(byToken, excluded, name, TravelerPreferenceKeys.InterestSourceTag);
        }

        var ordered = byToken.Values
            .OrderBy(option => option.Key, StringComparer.Ordinal)
            .ToArray();
        return new InterestOptionCatalog(ordered);
    }

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

    private static void Add(
        Dictionary<string, InterestOptionDto> byToken,
        HashSet<string> excluded,
        string name,
        string source)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var key = name.Trim();
        var token = TravelerPreferenceScoring.NormalizePreferenceToken(key);
        if (token.Length == 0 || excluded.Contains(token) || byToken.ContainsKey(token))
        {
            return;
        }

        byToken[token] = new InterestOptionDto(key, source);
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
