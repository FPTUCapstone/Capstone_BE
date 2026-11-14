using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;

/// <summary>
/// UC-10 AI level 1 (2026-11-11): the gate blends a real AI score exactly like production
/// (<c>BaseWeight × base + AiWeight × ai</c>) and falls back to the configured AI fallback score otherwise.
/// </summary>
public sealed class WeightedGateAiRankingTests
{
    private static readonly PersonalizationRankingOptions Ranking = new();

    [Fact]
    public void Personalize_BlendsTheProviderAiScore_LikeProduction()
    {
        var (candidate, persona) = Sample();

        var withAi = WeightedGateExecutor.Personalize(candidate, persona, new PersonalizationBaseScorer(Ranking), Ranking, aiScore: 0.9m);

        withAi.EffectiveDesirabilityScore.Should().Be(
            (Ranking.BaseWeight * withAi.TripMateBaseScore!.Value) + (Ranking.AiWeight * 0.9m));
    }

    [Fact]
    public void Personalize_UsesThePersonalizedFallback_WhenNoAiScoreIsGiven()
    {
        var (candidate, persona) = Sample();

        var fallback = WeightedGateExecutor.Personalize(candidate, persona, new PersonalizationBaseScorer(Ranking), Ranking, aiScore: null);

        fallback.EffectiveDesirabilityScore.Should().Be(
            (Ranking.BaseWeight * fallback.TripMateBaseScore!.Value) + (Ranking.AiWeight * Ranking.PersonalizedAiFallbackScore));
    }

    private static (GenerationCandidate Candidate, BenchmarkPersonaData Persona) Sample()
    {
        var data = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
        var persona = data.Personas.First(p => p.Tags.Count > 0);
        var candidate = SchedulingBenchmarkScenarioFactory.CreateScenario(data).Candidates.First();
        return (candidate, persona);
    }
}