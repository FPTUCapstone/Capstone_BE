using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public sealed class RecordedPoiRankingProvider : IPoiRankingProvider
{
    private readonly BgeM3CanonicalFixtureDocument _document;
    private readonly string? _overridePersonaId;

    public RecordedPoiRankingProvider(string? overridePersonaId = null)
        : this(
            BgeM3BenchmarkFixture.LoadAndVerify(
                SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData()),
            overridePersonaId)
    {
    }

    public RecordedPoiRankingProvider(
        VerifiedBgeM3Fixture fixture,
        string? overridePersonaId = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _document = fixture.Document;
        _overridePersonaId = overridePersonaId;
    }

    public Task<Result<PoiRankingResult>> RankAsync(
        PoiRankingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var personaId = _overridePersonaId
            ?? ResolvePersonaIdFromTokens(request.Context.PreferenceTokens);

        var scoreMap = _document.Personas
            .Single(persona => string.Equals(persona.PersonaId, personaId, StringComparison.OrdinalIgnoreCase))
            .Scores.ToDictionary(pair => long.Parse(pair.Key), pair => pair.Value);

        var rankedItems = request.Candidates.Select(candidate =>
        {
            if (!scoreMap.TryGetValue(candidate.PoiId, out var score))
            {
                throw new InvalidOperationException(
                    $"Missing recorded BGE-M3 score for persona '{personaId}', candidate POI ID {candidate.PoiId} ('{candidate.Name}').");
            }

            return new PoiRankingItem(candidate.PoiId, score, "Recorded BGE-M3 match");
        }).ToList();

        return Task.FromResult(Result.Success(new PoiRankingResult(rankedItems)));
    }

    private string ResolvePersonaIdFromTokens(IReadOnlyCollection<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return _document.Personas.Single(persona => persona.PreferenceTokens.Count == 0).PersonaId;
        }

        var normalizedTokens = tokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var persona = _document.Personas.FirstOrDefault(candidate =>
            candidate.PreferenceTokens.Count == normalizedTokens.Count
            && candidate.PreferenceTokens.All(normalizedTokens.Contains));

        return persona?.PersonaId
            ?? throw new InvalidOperationException(
                $"No recorded persona matches the provided preference token set: [{string.Join(", ", tokens)}]. Unknown tokens must not map to Generalist.");
    }
}