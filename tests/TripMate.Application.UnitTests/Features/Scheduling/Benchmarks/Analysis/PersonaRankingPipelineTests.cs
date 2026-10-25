using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Infrastructure.Persistence;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public class PersonaRankingPipelineTests
{
    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task AiMode_CallsPoiRankingProvider_AndFeedsIntoPoiRankingOrchestrator()
    {
        var spyProvider = new SpyPoiRankingProvider(aiScoreToReturn: 0.85m);
        var options = new PersonalizationRankingOptions { BaseWeight = 0.60m, AiWeight = 0.40m };
        using var dbContext = CreateDbContext();
        var orchestrator = new PoiRankingOrchestrator(
            new PersonalBehaviorFeatureAggregator(dbContext),
            spyProvider,
            options,
            providerEnabled: true,
            NullLogger<PoiRankingOrchestrator>.Instance);

        var candidate = new PoiRankingInputCandidate(1, 10, "Test POI", "Culture", ["culture"], 8m, 8m, 1.0m, 10000m);
        var input = new PoiRankingInput(1001, ["culture"], [candidate]);

        var snapshot = await orchestrator.BuildRankingAsync(input, CancellationToken.None);

        spyProvider.RankCallCount.Should().Be(1, "AI mode must call IPoiRankingProvider");
        snapshot.OutcomeCategory.Should().Be(PoiRankingOutcomeCategory.Success);

        var entry = snapshot.Entries[1];
        var expectedBlend = (options.BaseWeight * entry.TripMateBaseScore) + (options.AiWeight * 0.85m);
        entry.EffectiveDesirabilityScore.Should().Be(expectedBlend);
    }

    [Fact]
    public async Task BlendScore_UsesConfiguredRankingOptions_NotHardcodedNumbers()
    {
        var customOptions = new PersonalizationRankingOptions
        {
            BaseWeight = 0.70m,
            AiWeight = 0.30m
        };
        var spyProvider = new SpyPoiRankingProvider(aiScoreToReturn: 0.90m);
        using var dbContext = CreateDbContext();
        var orchestrator = new PoiRankingOrchestrator(
            new PersonalBehaviorFeatureAggregator(dbContext),
            spyProvider,
            customOptions,
            providerEnabled: true,
            NullLogger<PoiRankingOrchestrator>.Instance);

        var candidate = new PoiRankingInputCandidate(1, 10, "Test POI", "Culture", ["culture"], 8m, 8m, 1.0m, 10000m);
        var input = new PoiRankingInput(1001, ["culture"], [candidate]);

        var snapshot = await orchestrator.BuildRankingAsync(input, CancellationToken.None);

        var entry = snapshot.Entries[1];
        var expectedBlend = (0.70m * entry.TripMateBaseScore) + (0.30m * 0.90m);
        entry.EffectiveDesirabilityScore.Should().Be(expectedBlend);
    }

    [Fact]
    public async Task ProductionFallback_AppliesPersonalized060_AndDefault050()
    {
        var options = new PersonalizationRankingOptions
        {
            PersonalizedAiFallbackScore = 0.60m,
            DefaultAiFallbackScore = 0.50m
        };
        var spyProvider = new SpyPoiRankingProvider(aiScoreToReturn: 0.99m);
        using var dbContext = CreateDbContext();
        var orchestrator = new PoiRankingOrchestrator(
            new PersonalBehaviorFeatureAggregator(dbContext),
            spyProvider,
            options,
            providerEnabled: false,
            NullLogger<PoiRankingOrchestrator>.Instance);

        var candidate = new PoiRankingInputCandidate(1, 10, "Test POI", "Culture", ["culture"], 8m, 8m, 1.0m, 10000m);

        // 1. Personalized with tokens
        var personalizedInput = new PoiRankingInput(1001, ["culture"], [candidate]);
        var pSnapshot = await orchestrator.BuildRankingAsync(personalizedInput, CancellationToken.None);
        var pExpected = (options.BaseWeight * pSnapshot.Entries[1].TripMateBaseScore) + (options.AiWeight * 0.60m);
        pSnapshot.Entries[1].EffectiveDesirabilityScore.Should().Be(pExpected);

        // 2. Default with empty tokens
        var defaultInput = new PoiRankingInput(1001, [], [candidate]);
        var dSnapshot = await orchestrator.BuildRankingAsync(defaultInput, CancellationToken.None);
        var dExpected = (options.BaseWeight * dSnapshot.Entries[1].TripMateBaseScore) + (options.AiWeight * 0.50m);
        dSnapshot.Entries[1].EffectiveDesirabilityScore.Should().Be(dExpected);

        spyProvider.RankCallCount.Should().Be(0, "provider must not be called when disabled");
    }

    [Fact]
    public void BaseOnlyAblation_UsesRawBaseScore_WithoutAiWeightOrFallback()
    {
        var rawCandidate = new GenerationCandidate(
            1, "Cham Museum", new RoutePoint(16.0m, 108.0m), 60, 50000m, [],
            CategoryName: "Museum",
            TripMateBaseScore: 0.72m,
            EffectiveDesirabilityScore: null);

        var ablationCandidate = rawCandidate with { EffectiveDesirabilityScore = rawCandidate.TripMateBaseScore };

        ablationCandidate.EffectiveDesirabilityScore.Should().Be(0.72m);
        ablationCandidate.EffectiveDesirabilityScore.Should().NotBe(
            (0.60m * 0.72m) + (0.40m * 0.60m),
            "base-only ablation must not apply AI fallback");
    }

    [Fact]
    public void RecordedBgeM3Fixture_ContainsAll19Pois_For5Personas_WithProvenance()
    {
        BgeM3BenchmarkFixture.ModelName.Should().Be("bge-m3:latest");
        BgeM3BenchmarkFixture.ScenarioId.Should().Be("danang-real-1day");
        BgeM3BenchmarkFixture.PersonaCount.Should().Be(5);
        BgeM3BenchmarkFixture.PoiCount.Should().Be(40);

        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var document = BgeM3BenchmarkFixture.LoadAndVerify(cityData).Document;
        var cultureScores = document.Personas.Single(persona => persona.PersonaId == "CultureEnthusiast").Scores;
        cultureScores.Should().HaveCount(40);
        cultureScores["1"].Should().BeGreaterThan(0.70m, "Cham Museum is top cultural match");

        var generalistScores = document.Personas.Single(persona => persona.PersonaId == "DefaultGeneralist").Scores;
        generalistScores.Values.Should().OnlyContain(score => score == 0.50m, "cold start without tokens yields neutral 0.5");
    }

    [Fact]
    public async Task RecordedPoiRankingProvider_IsDeterministic_AndRequiresNoNetwork()
    {
        var provider = new RecordedPoiRankingProvider();
        var candidate = new PoiRankingCandidate(1, "Cham Museum", "Museum", ["culture", "museum"]);
        var request = new PoiRankingRequest([candidate], new PoiRankingContext(["culture", "museum", "history", "heritage", "art"]));

        var result1 = await provider.RankAsync(request, CancellationToken.None);
        var result2 = await provider.RankAsync(request, CancellationToken.None);

        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        result1.Value.Ranked.Single().AiScore.Should().Be(result2.Value.Ranked.Single().AiScore);
    }

    [Fact]
    public async Task PoiRankingOrchestrator_WhenProviderTimesOutOrFails_ProducesFallbackScoreAndOutcomeCategory()
    {
        var networkFailProvider = new FailingTestPoiRankingProvider(PoiRankingProviderErrorCodes.Network, "Provider network error");
        var options = new PersonalizationRankingOptions
        {
            PersonalizedAiFallbackScore = 0.60m,
            DefaultAiFallbackScore = 0.50m
        };
        using var dbContext = CreateDbContext();
        var orchestrator = new PoiRankingOrchestrator(
            new PersonalBehaviorFeatureAggregator(dbContext),
            networkFailProvider,
            options,
            providerEnabled: true,
            NullLogger<PoiRankingOrchestrator>.Instance);

        var candidate = new PoiRankingInputCandidate(1, 10, "Test POI", "Culture", ["culture"], 8m, 8m, 1.0m, 10000m);
        var input = new PoiRankingInput(1001, ["culture"], [candidate]);

        var snapshot = await orchestrator.BuildRankingAsync(input, CancellationToken.None);

        snapshot.OutcomeCategory.Should().Be(PoiRankingOutcomeCategory.Network);
        var expectedBlend = (options.BaseWeight * snapshot.Entries[1].TripMateBaseScore) + (options.AiWeight * 0.60m);
        snapshot.Entries[1].EffectiveDesirabilityScore.Should().Be(expectedBlend);
    }

    [Fact]
    public void WeightedRequestInputsFactory_PropagatesEffectiveDesirabilityScore_ToCspSolverInputs()
    {
        var factory = new WeightedRequestInputsFactory();
        var candidate = new GenerationCandidate(
            1, "Test POI", new RoutePoint(16.0m, 108.0m), 60, 50000m, [],
            CategoryName: "Culture",
            TripMateBaseScore: 0.50m,
            EffectiveDesirabilityScore: 0.88m);

        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData());
        var input = scenario.Input with { Candidates = [candidate] };

        var weighted = factory.Create([candidate], null, input, PoiRankingOutcomeCategory.Success);

        weighted.BaseByPoiId.Should().ContainKey(1L);
        weighted.BaseByPoiId[1L].Should().Be(0.88, "WeightedRequestInputs must use EffectiveDesirabilityScore as the base desirability for CSP solver");
    }

    private sealed class FailingTestPoiRankingProvider(string errorCode, string errorMessage) : IPoiRankingProvider
    {
        public Task<Result<PoiRankingResult>> RankAsync(PoiRankingRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Failure<PoiRankingResult>(errorCode, errorMessage));
    }

    private sealed class SpyPoiRankingProvider(decimal aiScoreToReturn) : IPoiRankingProvider
    {
        public int RankCallCount { get; private set; }

        public Task<Result<PoiRankingResult>> RankAsync(PoiRankingRequest request, CancellationToken cancellationToken)
        {
            RankCallCount++;
            var ranked = request.Candidates.Select(c => new PoiRankingItem(c.PoiId, aiScoreToReturn, "Spy matched")).ToList();
            return Task.FromResult(Result.Success(new PoiRankingResult(ranked)));
        }
    }
}