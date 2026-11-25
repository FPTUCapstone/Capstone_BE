using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public static class BgeM3CanonicalFixtureValidator
{
    public static void Validate(CityBenchmarkScenarioData cityData, BgeM3CanonicalFixtureDocument doc)
    {
        ArgumentNullException.ThrowIfNull(cityData);
        ArgumentNullException.ThrowIfNull(doc);

        if (!string.Equals(doc.SchemaVersion, "1.0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported fixture schema version '{doc.SchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(doc.ModelName)
            || string.IsNullOrWhiteSpace(doc.EndpointMode)
            || string.IsNullOrWhiteSpace(doc.Provenance))
        {
            throw new InvalidOperationException("Fixture model and provenance fields must not be empty.");
        }

        if (doc.CaptureTimestamp == default)
        {
            throw new InvalidOperationException("Fixture capture timestamp is invalid.");
        }

        if (!string.Equals(doc.ScenarioId, cityData.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Scenario ID mismatch: expected '{cityData.Id}', got '{doc.ScenarioId}'.");
        }

        if (doc.Personas.Select(p => p.PersonaId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != doc.Personas.Count)
        {
            throw new InvalidOperationException("Duplicate persona ID in fixture.");
        }

        var expectedPersonaIds = cityData.Personas.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualPersonaIds = doc.Personas.Select(p => p.PersonaId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!actualPersonaIds.SetEquals(expectedPersonaIds))
        {
            throw new InvalidOperationException(
                $"Missing or extra persona in fixture. Expected: [{string.Join(", ", expectedPersonaIds)}], Got: [{string.Join(", ", actualPersonaIds)}].");
        }

        var expectedPoiKeys = cityData.Pois.Select(p => p.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var expectedPersona in cityData.Personas)
        {
            var entry = doc.Personas.Single(p => string.Equals(p.PersonaId, expectedPersona.Id, StringComparison.OrdinalIgnoreCase));

            var expectedTokens = expectedPersona.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actualTokens = entry.PreferenceTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (entry.PreferenceTokens.Count != expectedPersona.Tags.Count || !actualTokens.SetEquals(expectedTokens))
            {
                throw new InvalidOperationException(
                    $"Token set mismatch for persona '{expectedPersona.Id}'. Expected: [{string.Join(", ", expectedTokens)}], Got: [{string.Join(", ", actualTokens)}].");
            }

            var actualPoiKeys = entry.Scores.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (entry.Scores.Count != cityData.Pois.Count || !actualPoiKeys.SetEquals(expectedPoiKeys))
            {
                throw new InvalidOperationException(
                    $"Missing or extra POI for persona '{expectedPersona.Id}'. Expected {cityData.Pois.Count} POIs, Got {entry.Scores.Count}.");
            }

            foreach (var (poiKey, score) in entry.Scores)
            {
                if (score is < 0.0m or > 1.0m)
                {
                    throw new InvalidOperationException(
                        $"Score {score} for POI {poiKey} in persona '{expectedPersona.Id}' is outside [0, 1].");
                }
            }
        }
    }
}