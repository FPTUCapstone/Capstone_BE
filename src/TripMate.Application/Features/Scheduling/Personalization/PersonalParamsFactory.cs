using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Personalization;

/// <summary>
/// Factory sinh các tham số cá nhân hóa (PersonalParams) từ TravelerProfile và cấu hình PersonalizationOptions.
/// </summary>
public sealed class PersonalParamsFactory(
    IOptions<PersonalizationOptions>? options = null,
    ILogger<PersonalParamsFactory>? logger = null)
{
    private readonly PersonalizationOptions _options = options?.Value ?? new PersonalizationOptions();

    public PersonalParams Create(TravelerProfile? profile)
    {
        var o = _options;
        var pace = profile?.TravelPace ?? TravelerPace.Moderate;
        if (!o.Pace.TryGetValue(pace, out var p))
        {
            p = o.Pace.TryGetValue(TravelerPace.Moderate, out var mod)
                ? mod
                : new PaceParams(LambdaPerMin: 0.015, MaxStopsPerDay: 5, MaxDailyEffort: 300, RestAfterMin: 120);
        }

        var riskScale = profile?.RiskTolerance switch
        {
            RiskToleranceLevel.Low => 0.7,
            RiskToleranceLevel.High => 1.2,
            _ => 1.0,
        };

        return new PersonalParams(
            LambdaPerMin: p.LambdaPerMin,
            Mu: o.Mu,
            MaxStopsPerDay: p.MaxStopsPerDay,
            MaxDailyEffort: (int)Math.Round(p.MaxDailyEffort * riskScale),
            RestAfterMin: p.RestAfterMin,
            MaxConsecutiveOutdoor: riskScale < 1.0 ? 2 : 3,
            SunriseFactor: 0.6, // Mặc định Normal (0.6) cho tới PR-05b
            GammaDefault: o.GammaDefault,
            GammaLoved: o.GammaLoved,
            LovedCategories: DeriveLovedCategories(profile?.InterestTagsJson, o.TagToCategory),
            RhoOutdoor: o.RhoOutdoor,
            RhoRest: o.RhoRest,
            RhoStops: o.RhoStops,
            RhoEffortPerHour: o.RhoEffortPerHour,
            ConfigVersion: o.Version);
    }

    private IReadOnlySet<string> DeriveLovedCategories(
        string? interestTagsJson,
        IReadOnlyDictionary<string, string> tagToCategory)
    {
        if (string.IsNullOrWhiteSpace(interestTagsJson) || tagToCategory.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var tags = JsonSerializer.Deserialize<string[]>(interestTagsJson);
            if (tags is null or { Length: 0 })
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var loved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawTag in tags)
            {
                if (string.IsNullOrWhiteSpace(rawTag))
                {
                    continue;
                }

                var tag = rawTag.Trim();
                if (tagToCategory.TryGetValue(tag, out var category) && !string.IsNullOrWhiteSpace(category))
                {
                    loved.Add(category.Trim());
                }
            }

            return loved;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Failed to parse InterestTagsJson: {Json}", interestTagsJson);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
