using System.Globalization;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public class PersonaAnalysisMarkdownRendererTests
{
    [Fact]
    public void Render_IsLocaleInvariant_UnderVietnameseAndInvariantCulture()
    {
        var metadata = new PersonaAnalysisReportMetadata("2026-10-21", "Release", "Windows", ".NET 10.0", "TestBox", 8, 0.60m, 0.40m, 0.60m, 0.50m);
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();

        var baselines = new List<PersonaBaselineResult>
        {
            new("CultureEnthusiast", "Culture Enthusiast", true, [1, 2, 3], 3, 30, 1, 0, 15000L),
            new("FoodLoverRelaxed", "Food Lover", true, [1, 4], 2, 20, 1, 0, 8000L)
        };
        var matrix = new double[2, 2] { { 1.0, 0.3333 }, { 0.3333, 1.0 } };
        var pairs = new List<PersonaJaccardPair>
        {
            new("CultureEnthusiast", "FoodLoverRelaxed", [1, 2, 3], [1, 4], 3, 2, 1, 4, 0.25)
        };
        var jaccardMatrix = new PersonaJaccardMatrix(["CultureEnthusiast", "FoodLoverRelaxed"], matrix, pairs);

        var runResult = new PersonaAnalysisRunResult(
            cityData,
            baselines,
            jaccardMatrix,
            Ablations: [],
            Sensitivities: [],
            PairsBelowThreshold: pairs,
            PairsAboveOrEqualThreshold: []);

        var originalCulture = CultureInfo.CurrentCulture;
        string renderedVi;
        string renderedInv;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
            renderedVi = PersonaAnalysisMarkdownRenderer.Render(metadata, runResult);

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            renderedInv = PersonaAnalysisMarkdownRenderer.Render(metadata, runResult);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        renderedVi.Should().Be(renderedInv, "renderer output must be strictly culture-invariant");
        renderedInv.Should().Contain("0.2500");
        renderedInv.Should().NotContain("0,2500");
    }

    [Fact]
    public void Render_DoesNotWriteAnyTrackedOrLocalFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tripmate-analysis-{Guid.NewGuid():N}");
        var artifactPath = Path.Combine(tempDir, "UC-10-persona-analysis-2026-10-21.md");
        var metadata = new PersonaAnalysisReportMetadata("2026-10-21", "Release", "test", ".NET", "test", 1, 0.60m, 0.40m, 0.60m, 0.50m);
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var runResult = new PersonaAnalysisRunResult(
            cityData,
            Baselines: [],
            new PersonaJaccardMatrix([], new double[0, 0], []),
            Ablations: [],
            Sensitivities: [],
            PairsBelowThreshold: [],
            PairsAboveOrEqualThreshold: []);

        var rendered = PersonaAnalysisMarkdownRenderer.Render(metadata, runResult);

        rendered.Should().NotBeNullOrWhiteSpace();
        File.Exists(artifactPath).Should().BeFalse("pure renderer must not perform file I/O");
        Directory.Exists(tempDir).Should().BeFalse();
    }

    [Fact]
    public void Render_DoesNotHardcodeDaNangResults_WhenGivenCustomScenario()
    {
        var metadata = new PersonaAnalysisReportMetadata(
            "2026-10-21",
            "Release",
            "test",
            ".NET",
            "test",
            1,
            0.60m,
            0.40m,
            0.60m,
            0.50m,
            EvaluationMode: "recorded",
            ModelName: "bge-m3:latest");

        var customCityData = new CityBenchmarkScenarioData(
            "custom-city-1day",
            "Custom Source",
            true,
            DateTimeOffset.UtcNow,
            "SE Asia Standard Time",
            new RoutePoint(21.0m, 105.0m),
            new RoutePoint(21.0m, 105.0m),
            480,
            TransportMode.Motorbike,
            RestPreference.Auto,
            1000000m,
            [101L],
            [
                new BenchmarkPoiData(101L, "Custom POI A", new RoutePoint(21.01m, 105.01m), "Culture", 60, 0m, IndoorOutdoorType.Indoor, true, 8m, 8m, ["art"], []),
                new BenchmarkPoiData(102L, "Custom POI B", new RoutePoint(21.02m, 105.02m), "Nature", 60, 0m, IndoorOutdoorType.Outdoor, false, 8m, 8m, ["nature"], [])
            ],
            [
                new BenchmarkPersonaData("CustomA", "Custom Persona A", TravelerPace.Moderate, RiskToleranceLevel.Medium, ["art"], ["Culture"], "Custom A"),
                new BenchmarkPersonaData("CustomB", "Custom Persona B", TravelerPace.Fast, RiskToleranceLevel.High, ["nature"], ["Nature"], "Custom B")
            ]);

        var baselines = new List<PersonaBaselineResult>
        {
            new("CustomA", "Custom Persona A", true, [101L, 102L], 2, 20, 2, 0, 5000L),
            new("CustomB", "Custom Persona B", true, [101L], 1, 10, 1, 0, 3000L)
        };
        var matrix = new double[2, 2] { { 1.0, 0.5 }, { 0.5, 1.0 } };
        var pairs = new List<PersonaJaccardPair>
        {
            new("CustomA", "CustomB", [101L, 102L], [101L], 2, 1, 1, 2, 0.50)
        };
        var jaccardMatrix = new PersonaJaccardMatrix(["CustomA", "CustomB"], matrix, pairs);

        var runResult = new PersonaAnalysisRunResult(
            customCityData,
            baselines,
            jaccardMatrix,
            Ablations: [],
            Sensitivities: [],
            PairsBelowThreshold: [],
            PairsAboveOrEqualThreshold: pairs);

        var markdown = PersonaAnalysisMarkdownRenderer.Render(metadata, runResult);

        // Must not contain hardcoded Da Nang POI names, IDs, or static narrative numbers
        markdown.Should().NotContain("5, 8, 1");
        markdown.Should().NotContain("Chợ Cồn");
        markdown.Should().NotContain("Madame Lân");
        markdown.Should().NotContain("Ngũ Hành Sơn");
        markdown.Should().NotContain("Dragon Bridge");
        markdown.Should().NotContain("Cham Museum");
        markdown.Should().NotContain("0.3333");
        markdown.Should().NotContain("0.4286");

        // Must contain custom scenario data
        markdown.Should().Contain("custom-city-1day");
        markdown.Should().Contain("CustomA");
        markdown.Should().Contain("CustomB");
    }

    [Fact]
    public void Render_ExplicitlyDisplaysEvaluationMode_LiveOrRecorded()
    {
        var recordedMeta = new PersonaAnalysisReportMetadata(
            "2026-10-21",
            "Release",
            "test",
            ".NET",
            "test",
            1,
            0.60m,
            0.40m,
            0.60m,
            0.50m,
            EvaluationMode: "recorded",
            FixtureSha256: BgeM3BenchmarkFixture.ExpectedFixtureSha256);
        var liveMeta = new PersonaAnalysisReportMetadata("2026-10-21", "Release", "test", ".NET", "test", 1, 0.60m, 0.40m, 0.60m, 0.50m, EvaluationMode: "live");
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var runResult = new PersonaAnalysisRunResult(
            cityData,
            Baselines: [],
            new PersonaJaccardMatrix([], new double[0, 0], []),
            Ablations: [],
            Sensitivities: [],
            PairsBelowThreshold: [],
            PairsAboveOrEqualThreshold: []);

        var recordedMd = PersonaAnalysisMarkdownRenderer.Render(recordedMeta, runResult);
        var liveMd = PersonaAnalysisMarkdownRenderer.Render(liveMeta, runResult);

        recordedMd.Should().Contain("Recorded");
        recordedMd.Should().Contain(BgeM3BenchmarkFixture.ExpectedFixtureSha256);

        liveMd.Should().Contain("Live");
    }

    [Fact]
    public void Render_ContainsNoUnsubstantiatedCausalClaims()
    {
        var metadata = new PersonaAnalysisReportMetadata("2026-10-21", "Release", "test", ".NET", "test", 1, 0.60m, 0.40m, 0.60m, 0.50m, EvaluationMode: "recorded");
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var pairs = new List<PersonaJaccardPair>
        {
            new("CultureEnthusiast", "FoodLoverRelaxed", [1, 2, 3], [1, 2], 3, 2, 2, 3, 0.6667)
        };
        var runResult = new PersonaAnalysisRunResult(
            cityData,
            Baselines: [],
            new PersonaJaccardMatrix(["CultureEnthusiast", "FoodLoverRelaxed"], new double[2, 2], pairs),
            Ablations: [],
            Sensitivities: [],
            PairsBelowThreshold: [],
            PairsAboveOrEqualThreshold: pairs);

        var markdown = PersonaAnalysisMarkdownRenderer.Render(metadata, runResult);

        // Spec requirement: renderer must not claim speculative causality without ablation evidence
        markdown.Should().NotContain("Root Cause:", "renderer should state structural observations rather than speculative causality");
        markdown.Should().Contain("Observed Structure:");
    }

    [Fact]
    public void Render_ArtifactHasNoFinalNewline()
    {
        var metadata = new PersonaAnalysisReportMetadata("2026-10-21", "Release", "test", ".NET", "test", 1, 0.60m, 0.40m, 0.60m, 0.50m);
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var runResult = new PersonaAnalysisRunResult(
            cityData,
            Baselines: [],
            new PersonaJaccardMatrix([], new double[0, 0], []),
            Ablations: [],
            Sensitivities: [],
            PairsBelowThreshold: [],
            PairsAboveOrEqualThreshold: []);

        var markdown = PersonaAnalysisMarkdownRenderer.Render(metadata, runResult);

        markdown.EndsWith("\n").Should().BeFalse("Markdown output must end at final content character without trailing LF");
        markdown.EndsWith("\r").Should().BeFalse("Markdown output must end at final content character without trailing CR");
    }

    [Theory]
    [InlineData(4, "4/5")]
    [InlineData(0, "0/5")]
    public void Render_ReportsNoCategoryDecayRouteChangesFromAblationData(int changedCount, string expected)
    {
        var result = CreateRendererResult(
            Enumerable.Range(0, 5).Select(index => new AblationVariantResult(
                "No category decay", "test", $"Persona{index}", true, [1],
                index < changedCount ? 0.5 : 1.0, [], [], 1, 10, 1, 0, 100, "test")).ToArray());

        var markdown = PersonaAnalysisMarkdownRenderer.Render(
            new PersonaAnalysisReportMetadata("2026-10-21", "Release", "test", ".NET", "test", 1, 0.60m, 0.40m, 0.60m, 0.50m),
            result);

        markdown.Should().Contain($"No category decay changed POI selection for {expected} personas.");
        markdown.Should().NotContain("without altering chosen stops");
    }

    [Fact]
    public void Render_UsesMetadataJaccardThresholdForEveryGateLabelAndDecision()
    {
        var pair = new PersonaJaccardPair("A", "B", [1, 2], [1], 2, 1, 1, 2, 0.45);
        var result = CreateRendererResult([], pair);
        var metadata = new PersonaAnalysisReportMetadata(
            "2026-10-21", "Release", "test", ".NET", "test", 1, 0.60m, 0.40m, 0.60m, 0.50m, JaccardThreshold: 0.50);

        var markdown = PersonaAnalysisMarkdownRenderer.Render(metadata, result);

        markdown.Should().Contain("PERSONALIZED PAIRS MEET JACCARD GATE (< 0.50)");
        markdown.Should().Contain("Personalized Pairs Meeting Gate (< 0.50)**: 1 / 1");
        markdown.Should().Contain("Personalized Gate (< 0.50)");
        markdown.Should().Contain("J < 0.50");
        markdown.Should().Contain("PASS");
        markdown.Should().NotContain("FAIL (>= 0.40)");
    }

    [Fact]
    public void Render_ReportsColdStartComparisonsWithoutApplyingPersonalizationGate()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var personalizedPair = new PersonaJaccardPair(
            "CultureEnthusiast", "FoodLoverRelaxed", [1, 2], [3], 2, 1, 0, 3, 0.0);
        var coldStartPair = new PersonaJaccardPair(
            "ScenicPhotography", "DefaultGeneralist", [1, 2], [1, 2], 2, 2, 2, 2, 1.0);
        var pairs = new[] { personalizedPair, coldStartPair };
        var result = new PersonaAnalysisRunResult(
            cityData,
            Baselines:
            [
                new PersonaBaselineResult(
                    "DefaultGeneralist", "Default Generalist", true, [1, 2], 2, 10, 2, 0, 100)
            ],
            new PersonaJaccardMatrix(
                ["CultureEnthusiast", "FoodLoverRelaxed", "ScenicPhotography", "DefaultGeneralist"],
                new double[4, 4],
                pairs),
            Ablations: [],
            Sensitivities: [],
            PairsBelowThreshold: [personalizedPair],
            PairsAboveOrEqualThreshold: [coldStartPair]);

        var markdown = PersonaAnalysisMarkdownRenderer.Render(
            new PersonaAnalysisReportMetadata(
                "2026-10-21", "Release", "test", ".NET", "test", 1,
                0.60m, 0.40m, 0.60m, 0.50m),
            result);

        markdown.Should().Contain("PASS — PERSONALIZED PAIRS MEET JACCARD GATE");
        markdown.Should().Contain("Cold-Start Comparison Pairs (observational)**: 1");
        markdown.Should().Contain("OBSERVED (not gated)");
        markdown.Should().NotContain("ScenicPhotography` vs `DefaultGeneralist`**: Jaccard = **1.0000** (Intersection");
    }

    private static PersonaAnalysisRunResult CreateRendererResult(
        IReadOnlyList<AblationVariantResult> ablations,
        PersonaJaccardPair? pair = null)
    {
        var pairs = pair is null ? Array.Empty<PersonaJaccardPair>() : [pair];
        return new PersonaAnalysisRunResult(
            SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData(),
            Baselines:
            [
                new PersonaBaselineResult(
                    "DefaultGeneralist", "Default Generalist", true, [1, 2], 2, 10, 2, 0, 100)
            ],
            new PersonaJaccardMatrix(pair is null ? [] : ["A", "B"], new double[pair is null ? 0 : 2, pair is null ? 0 : 2], pairs),
            ablations,
            Sensitivities: [],
            PairsBelowThreshold: [],
            PairsAboveOrEqualThreshold: pairs);
    }
}