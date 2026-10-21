using System.Globalization;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public class PersonaAnalysisRunnerTests
{
    [Fact]
    public async Task RunAsync_ProductionFallbackDescription_UsesAuthoritativeOptions()
    {
        var rankingOptions = new PersonalizationRankingOptions
        {
            BaseWeight = 0.73m,
            AiWeight = 0.27m,
            PersonalizedAiFallbackScore = 0.61m,
            DefaultAiFallbackScore = 0.49m
        };

        var result = await PersonaAnalysisRunner.RunAsync(rankingOptions: rankingOptions);

        result.Ablations
            .Where(ablation => ablation.VariantName == "Production fallback")
            .Should().OnlyContain(ablation =>
                ablation.Description == "Production fallback AI score (0.61 for personalized, 0.49 for default) with provider disabled");
    }

    [Fact]
    public async Task RunAsync_LiveCallsProviderExactlyOncePerPersonaWithPreferenceTokens()
    {
        var provider = new OneCallPerPreferenceSetProvider();

        var result = await PersonaAnalysisRunner.RunAsync(
            SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData(),
            provider,
            evaluationMode: "live");

        result.Baselines.Should().HaveCount(5);
        provider.CallsByPreferenceSet.Should().HaveCount(4);
        provider.CallsByPreferenceSet.Values.Should().OnlyContain(count => count == 1);
    }

    [Fact]
    public async Task RunAsync_ReusesAuthoritativePersonalizedScoresForBaselineAblationAndSensitivity()
    {
        var provider = new FailOnRepeatedPreferenceSetProvider();

        var result = await PersonaAnalysisRunner.RunAsync(
            SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData(),
            provider,
            evaluationMode: "live");

        result.Baselines.Should().OnlyContain(value => value.Feasible);
        result.Ablations.Should().HaveCount(30);
        result.Sensitivities.Should().HaveCount(105);
        provider.CallCount.Should().Be(4);
    }

    [Fact]
    public async Task RunAsync_BuildsSymmetricMatrixAndStructuredResults()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
        var originalCulture = CultureInfo.CurrentCulture;
        PersonaAnalysisRunResult result;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
            result = await PersonaAnalysisRunner.RunAsync(cityData, jaccardThreshold: 0.50);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        result.Baselines.Should().HaveCount(5);
        result.Baselines.Should().OnlyContain(b => b.Feasible);

        // Verify 5x5 matrix properties
        var matrix = result.JaccardMatrix;
        matrix.PersonaIds.Should().HaveCount(5);
        for (var i = 0; i < 5; i++)
        {
            matrix.Matrix[i, i].Should().Be(1.0, "diagonal of Jaccard matrix must be 1.00");
            for (var j = 0; j < 5; j++)
            {
                matrix.Matrix[i, j].Should().Be(matrix.Matrix[j, i], "matrix must be symmetric");
                matrix.Matrix[i, j].Should().BeInRange(0.0, 1.0);
            }
        }

        // Pairs
        matrix.Pairs.Should().HaveCount(10);
        (result.PairsBelowThreshold.Count + result.PairsAboveOrEqualThreshold.Count).Should().Be(10);
        result.PairsBelowThreshold.Should().OnlyContain(pair => pair.Jaccard < 0.50);
        result.PairsAboveOrEqualThreshold.Should().OnlyContain(pair => pair.Jaccard >= 0.50);

        // Ablations: 5 personas * 6 variants = 30
        result.Ablations.Should().HaveCount(30);
        result.Ablations.Select(a => a.VariantName).Distinct().Should().BeEquivalentTo(
            "Base-only ablation",
            "Production fallback",
            "No category decay",
            "No soft rho penalties",
            "No travel penalty",
            "No TimeFit");

        // Sensitivities: 5 personas * (3 + 3 + 3 + 3 + 3 + 3 + 3) = 105 probe points
        result.Sensitivities.Should().HaveCount(105);
        result.Sensitivities.Select(s => s.ParameterName).Distinct().Should().BeEquivalentTo(
            "LambdaPerMin",
            "GammaDefault",
            "GammaLoved",
            "RhoStops",
            "RhoOutdoor",
            "RhoRest",
            "RhoEffortPerHour");
        result.Sensitivities.Select(value => value.Notes).Should().NotContain(note => note.Contains("0,5x"));
        result.Sensitivities.Select(value => value.Notes).Should().Contain(note => note.Contains("0.5x"));
    }

    [Fact]
    public async Task RunAsync_IsDeterministic_AcrossTwoExecutions()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();

        var run1 = await PersonaAnalysisRunner.RunAsync(cityData);
        var run2 = await PersonaAnalysisRunner.RunAsync(cityData);

        run1.Baselines.Select(b => string.Join(',', b.VisitedPoiIds))
            .Should().Equal(run2.Baselines.Select(b => string.Join(',', b.VisitedPoiIds)));

        run1.JaccardMatrix.Pairs.Select(p => p.Jaccard)
            .Should().Equal(run2.JaccardMatrix.Pairs.Select(p => p.Jaccard));
    }

    [Fact]
    public async Task PersonalizeCandidatesAsync_PropagatesOutcomeCategory_AndFallbackStatus()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(cityData);
        var provider = new RecordedPoiRankingProvider();
        var rankingOptions = new PersonalizationRankingOptions();

        // 1. Personalized persona with tokens -> Success, FallbackUsed = false
        var culturePersona = cityData.Personas.Single(p => p.Id == "CultureEnthusiast");
        var pResult = await PersonaAnalysisRunner.PersonalizeCandidatesAsync(
            scenario.Candidates,
            culturePersona,
            provider,
            rankingOptions,
            providerEnabled: true,
            providerMode: "recorded",
            CancellationToken.None);

        pResult.OutcomeCategory.Should().Be(PoiRankingOutcomeCategory.Success);
        pResult.FallbackUsed.Should().BeFalse();
        pResult.ProviderMode.Should().Be("recorded");

        // 2. Cold-start persona without tokens -> ProviderSkipped, FallbackUsed = true
        var generalistPersona = cityData.Personas.Single(p => p.Id == "DefaultGeneralist");
        var gResult = await PersonaAnalysisRunner.PersonalizeCandidatesAsync(
            scenario.Candidates,
            generalistPersona,
            provider,
            rankingOptions,
            providerEnabled: true,
            providerMode: "recorded",
            CancellationToken.None);

        gResult.OutcomeCategory.Should().Be(PoiRankingOutcomeCategory.ProviderSkipped);
        gResult.FallbackUsed.Should().BeTrue();

        // 3. Provider disabled (Production fallback mode) -> ProviderSkipped, FallbackUsed = true
        var fResult = await PersonaAnalysisRunner.PersonalizeCandidatesAsync(
            scenario.Candidates,
            culturePersona,
            provider,
            rankingOptions,
            providerEnabled: false,
            providerMode: "recorded",
            CancellationToken.None);

        fResult.OutcomeCategory.Should().Be(PoiRankingOutcomeCategory.ProviderSkipped);
        fResult.FallbackUsed.Should().BeTrue();
    }

    private class OneCallPerPreferenceSetProvider : IPoiRankingProvider
    {
        public Dictionary<string, int> CallsByPreferenceSet { get; } = [];

        public virtual Task<Result<PoiRankingResult>> RankAsync(
            PoiRankingRequest request,
            CancellationToken cancellationToken)
        {
            var key = string.Join('|', request.Context.PreferenceTokens.OrderBy(value => value, StringComparer.Ordinal));
            CallsByPreferenceSet[key] = CallsByPreferenceSet.GetValueOrDefault(key) + 1;
            return Task.FromResult(Result.Success(new PoiRankingResult(
                request.Candidates.Select(candidate => new PoiRankingItem(candidate.PoiId, 0.75m, "test")).ToArray())));
        }
    }

    private sealed class FailOnRepeatedPreferenceSetProvider : OneCallPerPreferenceSetProvider
    {
        public int CallCount { get; private set; }

        public override Task<Result<PoiRankingResult>> RankAsync(
            PoiRankingRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var key = string.Join('|', request.Context.PreferenceTokens.OrderBy(value => value, StringComparer.Ordinal));
            if (CallsByPreferenceSet.ContainsKey(key))
            {
                return Task.FromResult(Result.Failure<PoiRankingResult>(
                    PoiRankingProviderErrorCodes.Network,
                    "provider must not be called twice for one authoritative persona snapshot"));
            }

            return base.RankAsync(request, cancellationToken);
        }
    }
}