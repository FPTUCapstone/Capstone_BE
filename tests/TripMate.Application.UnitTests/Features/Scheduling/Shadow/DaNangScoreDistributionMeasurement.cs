using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Shadow;

public class DaNangScoreDistributionMeasurement
{
    // Kept as a public diagnostic input while the shared benchmark source uses BenchmarkPersonaData.
    public sealed record PersonaDefinition(
        string Id,
        string DisplayName,
        TravelerPace Pace,
        RiskToleranceLevel Risk,
        string[] Tags,
        string[] LovedCategories,
        string Description);

    // Real bge-m3 semantic scores measured via Capstone_AI on Ollama for the 16-POI baseline.
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<long, double>> BgeM3Scores =
        new Dictionary<string, IReadOnlyDictionary<long, double>>
        {
            ["CultureEnthusiast"] = new Dictionary<long, double>
            {
                [1] = 0.7625,
                [2] = 0.7531,
                [3] = 0.7073,
                [4] = 0.6647,
                [5] = 0.8836,
                [6] = 0.8029,
                [7] = 0.8260,
                [8] = 0.5629,
                [9] = 0.4037,
                [10] = 0.8364,
                [11] = 0.7904,
                [12] = 0.5809,
                [13] = 0.6129,
                [14] = 0.7610,
                [15] = 0.3274,
                [16] = 0.2213
            },
            ["FoodLoverRelaxed"] = new Dictionary<long, double>
            {
                [1] = 0.3653,
                [2] = 0.3683,
                [3] = 0.3885,
                [4] = 0.3320,
                [5] = 0.6781,
                [6] = 0.6775,
                [7] = 0.6833,
                [8] = 0.6141,
                [9] = 0.4637,
                [10] = 0.8904,
                [11] = 0.5594,
                [12] = 0.5525,
                [13] = 0.5000,
                [14] = 0.4588,
                [15] = 0.7786,
                [16] = 0.6616
            },
            ["ActiveExplorerFast"] = new Dictionary<long, double>
            {
                [1] = 0.4659,
                [2] = 0.4599,
                [3] = 0.4496,
                [4] = 0.3759,
                [5] = 0.6621,
                [6] = 0.5518,
                [7] = 0.6303,
                [8] = 0.8721,
                [9] = 0.7416,
                [10] = 0.6268,
                [11] = 0.7400,
                [12] = 0.9759,
                [13] = 0.9613,
                [14] = 0.5659,
                [15] = 0.3744,
                [16] = 0.3019
            },
            ["ScenicPhotography"] = new Dictionary<long, double>
            {
                [1] = 0.4645,
                [2] = 0.4615,
                [3] = 0.6592,
                [4] = 0.4290,
                [5] = 0.8751,
                [6] = 0.8099,
                [7] = 0.8472,
                [8] = 0.8713,
                [9] = 0.8307,
                [10] = 0.5761,
                [11] = 0.8132,
                [12] = 0.8165,
                [13] = 0.7504,
                [14] = 0.5587,
                [15] = 0.3413,
                [16] = 0.2840
            },
            ["DefaultGeneralist"] = Enumerable.Range(1, 16)
                .ToDictionary(id => (long)id, _ => 0.5000)
        };

    [Fact]
    public void Measure_ScoreDistributions_DaNang_UsesSharedBaselineWithoutWritingArtifacts()
    {
        var data = CreateCityDataForMeasurement();
        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(data);
        var scorer = new PersonalizationBaseScorer(new PersonalizationRankingOptions());

        scenario.Candidates.Should().HaveCount(16);
        data.Personas.Select(persona => persona.Id).Should().Equal(BgeM3Scores.Keys);

        foreach (var persona in data.Personas)
        {
            var measured = BgeM3Scores[persona.Id];
            measured.Keys.Should().BeEquivalentTo(scenario.Candidates.Select(candidate => candidate.Id));

            var baseScores = scenario.Candidates.Select(candidate => scorer.Score(
                PersonalizationFeatureBuilder.Build(
                    persona.Tags,
                    candidate.CategoryName!,
                    candidate.Tags ?? [],
                    candidate.ScenicScore,
                    candidate.PhotoRating),
                personalBehaviorAffinity: 0.5m));

            baseScores.Should().OnlyContain(score => score >= 0m && score <= 1m);
        }
    }

    [Fact]
    public async Task AiFallback_DoesNotReduceTheSharedBaselineByMoreThanOneStop()
    {
        var data = CreateCityDataForMeasurement();
        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(data);
        var ranking = new PersonalizationRankingOptions();
        var scorer = new PersonalizationBaseScorer(ranking);
        var weightedFactory = new WeightedRequestInputsFactory(new PersonalParamsFactory(
            Options.Create(SchedulingBenchmarkScenarioFactory.CreatePersonalizationOptions())));

        foreach (var persona in data.Personas)
        {
            var candidates = scenario.Candidates.Select(candidate =>
            {
                var baseScore = scorer.Score(
                    PersonalizationFeatureBuilder.Build(
                        persona.Tags,
                        candidate.CategoryName!,
                        candidate.Tags ?? [],
                        candidate.ScenicScore,
                        candidate.PhotoRating),
                    personalBehaviorAffinity: 0.5m);
                return (Candidate: candidate, BaseScore: baseScore);
            }).ToArray();
            var successCandidates = candidates.Select(item => item.Candidate with
            {
                TripMateBaseScore = item.BaseScore,
                EffectiveDesirabilityScore = ranking.BaseWeight * item.BaseScore
                    + ranking.AiWeight * (decimal)BgeM3Scores[persona.Id][item.Candidate.Id]
            }).ToArray();
            var fallbackAiScore = persona.Tags.Count > 0
                ? ranking.PersonalizedAiFallbackScore
                : ranking.DefaultAiFallbackScore;
            var fallbackCandidates = candidates.Select(item => item.Candidate with
            {
                TripMateBaseScore = item.BaseScore,
                EffectiveDesirabilityScore = ranking.BaseWeight * item.BaseScore
                    + ranking.AiWeight * fallbackAiScore
            }).ToArray();

            var successStops = await SolveStopCountAsync(
                scenario,
                persona,
                successCandidates,
                PoiRankingOutcomeCategory.Success,
                weightedFactory);
            var fallbackStops = await SolveStopCountAsync(
                scenario,
                persona,
                fallbackCandidates,
                PoiRankingOutcomeCategory.Timeout,
                weightedFactory);

            AssertFallbackPreservesStops(persona.DisplayName, successStops, fallbackStops);
        }
    }

    internal static void AssertFallbackPreservesStops(string personaName, int successStops, int fallbackStops)
    {
        var reduction = successStops - fallbackStops;
        fallbackStops.Should().BeGreaterThanOrEqualTo(
            successStops - 1,
            $"AI fallback must not reduce the itinerary by more than one stop for persona '{personaName}'; "
            + $"success={successStops}, fallback={fallbackStops}, reduction={reduction}");
    }

    [Fact]
    public void HardGate_WhenFallbackLosesTwoOrMoreStops_Fails()
    {
        var action = () => AssertFallbackPreservesStops("SyntheticPersona", successStops: 6, fallbackStops: 4);

        action.Should().Throw<Exception>()
            .WithMessage("*must not reduce the itinerary by more than one stop*reduction=2*");
    }

    private static async Task<int> SolveStopCountAsync(
        TripMate.Application.UnitTests.Features.Scheduling.Golden.GoldenScenario scenario,
        BenchmarkPersonaData persona,
        IReadOnlyList<GenerationCandidate> candidates,
        PoiRankingOutcomeCategory outcome,
        WeightedRequestInputsFactory weightedFactory)
    {
        var input = scenario.Input with { Candidates = candidates };
        var weighted = weightedFactory.Create(
            candidates,
            SchedulingBenchmarkScenarioFactory.CreateProfile(persona, input.StartAtUtc),
            input,
            outcome);
        var options = new SchedulingGenerationOptions
        {
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                WeightedMaxNodes = 50_000,
                TimeLimitMilliseconds = 15_000
            }
        };
        var result = await new ItineraryGenerationService(
            new PrecomputedRouteDurationProvider(scenario.Matrix), options)
            .GenerateAsync(input, weighted, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        return result.Value.Items.Count(item => item.Kind == ItineraryItemKind.Visit);
    }

    private sealed class PrecomputedRouteDurationProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) =>
            Task.FromResult(matrix);
    }

    internal static CityBenchmarkScenarioData CreateCityDataForMeasurement() =>
        SchedulingBenchmarkScenarioFactory.CreateDaNangBaselineData();
}