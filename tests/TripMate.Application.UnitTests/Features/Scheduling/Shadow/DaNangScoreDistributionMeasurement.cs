using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;
using Xunit;
using Xunit.Abstractions;

namespace TripMate.Application.UnitTests.Features.Scheduling.Shadow;

public class DaNangScoreDistributionMeasurement(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    public sealed record PersonaDefinition(
        string Id,
        string DisplayName,
        TravelerPace Pace,
        RiskToleranceLevel Risk,
        string[] Tags,
        string[] LovedCategories,
        string Description);

    private static readonly PersonaDefinition[] Personas =
    [
        new(
            "CultureEnthusiast",
            "Culture Enthusiast",
            TravelerPace.Moderate,
            RiskToleranceLevel.Medium,
            ["culture", "museum", "history", "heritage", "art"],
            [PoiCategoryNames.Culture, PoiCategoryNames.Museum],
            "Ưu tiên di tích, bảo tàng, lịch sử; nhịp độ vừa phải (tối đa 5 điểm/ngày)."),
        new(
            "FoodLoverRelaxed",
            "Food Lover (Relaxed)",
            TravelerPace.Relaxed,
            RiskToleranceLevel.Low,
            ["food", "coffee", "cuisine", "culinary"],
            [PoiCategoryNames.FoodAndDrink],
            "Ưu tiên trải nghiệm ẩm thực, nghỉ ngơi; nhịp độ thong thả (tối đa 3 điểm/ngày)."),
        new(
            "ActiveExplorerFast",
            "Active Explorer (Fast)",
            TravelerPace.Fast,
            RiskToleranceLevel.High,
            ["nature", "adventure", "outdoor", "hiking", "natural attraction"],
            [PoiCategoryNames.Nature, "Natural attraction"],
            "Ưu tiên thiên nhiên ngoài trời, tốc độ nhanh (tối đa 8 điểm/ngày)."),
        new(
            "ScenicPhotography",
            "Scenic & Photography",
            TravelerPace.Moderate,
            RiskToleranceLevel.Low,
            ["photography", "scenic", "beach", "sightseeing", "nature"],
            [PoiCategoryNames.Culture, PoiCategoryNames.Nature, PoiCategoryNames.Beach],
            "Ưu tiên danh lam thắng cảnh, điểm chụp ảnh đẹp, nhịp độ vừa phải."),
        new(
            "DefaultGeneralist",
            "Default Generalist (Cold Start)",
            TravelerPace.Moderate,
            RiskToleranceLevel.Medium,
            [],
            [],
            "Khách vãng lai chưa có hồ sơ; áp dụng cấu hình mặc định của hệ thống.")
    ];

    // Real bge-m3 semantic scores measured via Capstone_AI on Ollama
    private static readonly Dictionary<string, Dictionary<long, double>> BgeM3Scores = new()
    {
        ["CultureEnthusiast"] = new()
        {
            [1L] = 0.7625, [2L] = 0.7531, [3L] = 0.7073, [4L] = 0.6647,
            [5L] = 0.8836, [6L] = 0.8029, [7L] = 0.8260, [8L] = 0.5629,
            [9L] = 0.4037, [10L] = 0.8364, [11L] = 0.7904, [12L] = 0.5809,
            [13L] = 0.6129, [14L] = 0.7610, [15L] = 0.3274, [16L] = 0.2213
        },
        ["FoodLoverRelaxed"] = new()
        {
            [1L] = 0.3653, [2L] = 0.3683, [3L] = 0.3885, [4L] = 0.3320,
            [5L] = 0.6781, [6L] = 0.6775, [7L] = 0.6833, [8L] = 0.6141,
            [9L] = 0.4637, [10L] = 0.8904, [11L] = 0.5594, [12L] = 0.5525,
            [13L] = 0.5000, [14L] = 0.4588, [15L] = 0.7786, [16L] = 0.6616
        },
        ["ActiveExplorerFast"] = new()
        {
            [1L] = 0.4659, [2L] = 0.4599, [3L] = 0.4496, [4L] = 0.3759,
            [5L] = 0.6621, [6L] = 0.5518, [7L] = 0.6303, [8L] = 0.8721,
            [9L] = 0.7416, [10L] = 0.6268, [11L] = 0.7400, [12L] = 0.9759,
            [13L] = 0.9613, [14L] = 0.5659, [15L] = 0.3744, [16L] = 0.3019
        },
        ["ScenicPhotography"] = new()
        {
            [1L] = 0.4645, [2L] = 0.4615, [3L] = 0.6592, [4L] = 0.4290,
            [5L] = 0.8751, [6L] = 0.8099, [7L] = 0.8472, [8L] = 0.8713,
            [9L] = 0.8307, [10L] = 0.5761, [11L] = 0.8132, [12L] = 0.8165,
            [13L] = 0.7504, [14L] = 0.5587, [15L] = 0.3413, [16L] = 0.2840
        },
        ["DefaultGeneralist"] = new()
        {
            [1L] = 0.5000, [2L] = 0.5000, [3L] = 0.5000, [4L] = 0.5000,
            [5L] = 0.5000, [6L] = 0.5000, [7L] = 0.5000, [8L] = 0.5000,
            [9L] = 0.5000, [10L] = 0.5000, [11L] = 0.5000, [12L] = 0.5000,
            [13L] = 0.5000, [14L] = 0.5000, [15L] = 0.5000, [16L] = 0.5000
        }
    };

    public sealed record DistributionSummary(
        double Min,
        double Median,
        double Max,
        double Mean,
        double StdDev);

    public sealed record PersonaMeasurementResult(
        string PersonaId,
        string DisplayName,
        DistributionSummary BaseScoreStats,
        DistributionSummary AiScoreStats,
        DistributionSummary BaseSuccessStats,
        DistributionSummary BaseFallbackStats,
        int SuccessStopsCount,
        int FallbackStopsCount,
        IReadOnlyList<long> SuccessVisitedPoiIds,
        IReadOnlyList<long> FallbackVisitedPoiIds,
        long? SuccessModelObj,
        long? FallbackModelObj,
        int SuccessTravelMin,
        int FallbackTravelMin);

    [Fact]
    public async Task Measure_ScoreDistributions_And_CompareStopsCount_DaNang()
    {
        var daNangScenario = CreateDaNangRealScenario();
        var rankingOptions = new PersonalizationRankingOptions();
        var baseScorer = new PersonalizationBaseScorer(rankingOptions);
        var personalizationOptions = CreatePersonalizationOptions();
        var paramsFactory = new PersonalParamsFactory(Microsoft.Extensions.Options.Options.Create(personalizationOptions));
        var weightedInputsFactory = new WeightedRequestInputsFactory(paramsFactory);

        var results = new List<PersonaMeasurementResult>();

        foreach (var persona in Personas)
        {
            var profile = CreateProfile(persona);
            var aiScores = BgeM3Scores[persona.Id];

            // 1. Calculate BaseScore for 16 POIs
            var baseScores = new Dictionary<long, double>();
            foreach (var c in daNangScenario.Candidates)
            {
                var prefTokens = persona.Tags;
                var cat = c.CategoryName;
                var tags = c.Tags ?? [];
                var features = PersonalizationFeatureBuilder.Build(
                    prefTokens,
                    cat,
                    tags,
                    c.ScenicScore ?? 7m,
                    c.PhotoRating ?? 7m);

                var baseDec = baseScorer.Score(features, personalBehaviorAffinity: 0.5m);
                baseScores[c.Id] = (double)baseDec;
            }

            // 2. Score distributions for BaseScore, AiScore, Success, and Fallback
            var baseList = daNangScenario.Candidates.Select(c => baseScores[c.Id]).ToList();
            var aiList = daNangScenario.Candidates.Select(c => aiScores[c.Id]).ToList();
            var baseWeightDouble = (double)rankingOptions.BaseWeight;
            var aiWeightDouble = (double)rankingOptions.AiWeight;
            var baseSuccessList = daNangScenario.Candidates.Select(c => baseWeightDouble * baseScores[c.Id] + aiWeightDouble * aiScores[c.Id]).ToList();
            var fallbackAiDouble = (double)(persona.Tags.Length > 0
                ? rankingOptions.PersonalizedAiFallbackScore
                : rankingOptions.DefaultAiFallbackScore);
            var baseFallbackList = daNangScenario.Candidates.Select(c => baseWeightDouble * baseScores[c.Id] + aiWeightDouble * fallbackAiDouble).ToList();

            var baseStats = ComputeStats(baseList);
            var aiStats = ComputeStats(aiList);
            var successStats = ComputeStats(baseSuccessList);
            var fallbackStats = ComputeStats(baseFallbackList);

            // 3. Solve itinerary for Case 1: AI Success
            var candidatesSuccess = daNangScenario.Candidates.Select(c =>
            {
                var baseVal = (decimal)baseScores[c.Id];
                var aiVal = (decimal)aiScores[c.Id];
                var effectiveVal = rankingOptions.BaseWeight * baseVal + rankingOptions.AiWeight * aiVal;
                return c with
                {
                    TripMateBaseScore = baseVal,
                    EffectiveDesirabilityScore = effectiveVal
                };
            }).ToList();

            var inputSuccess = daNangScenario.Input with { Candidates = candidatesSuccess };
            var weightedSuccess = weightedInputsFactory.Create(
                candidatesSuccess,
                profile,
                inputSuccess,
                PoiRankingOutcomeCategory.Success);

            var routeProvider = new PrecomputedRouteDurationProvider(daNangScenario.Matrix);
            var solverOptions = new SchedulingGenerationOptions
            {
                SolverMode = SchedulingSolverMode.Csp,
                Csp = new CspOptions
                {
                    Mode = ObjectiveMode.WeightedUtility,
                    WeightedMaxNodes = 50_000,
                    TimeLimitMilliseconds = 15_000,
                }
            };

            var serviceSuccess = new ItineraryGenerationService(routeProvider, solverOptions);
            var resultSuccess = await serviceSuccess.GenerateAsync(inputSuccess, weightedSuccess, CancellationToken.None);
            resultSuccess.IsSuccess.Should().BeTrue();
            var planSuccess = resultSuccess.Value;
            var successVisits = planSuccess.Items
                .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
                .OrderBy(i => i.SequenceNo)
                .ToList();

            var successModelObj = planSuccess.ModelObjective;
            var successTravel = CalculateTravelMinutes(successVisits);

            // 4. Solve itinerary for Case 2: AI Fallback (using options-configured fallback scores)
            var fallbackAiVal = persona.Tags.Length > 0
                ? rankingOptions.PersonalizedAiFallbackScore
                : rankingOptions.DefaultAiFallbackScore;
            var candidatesFallback = daNangScenario.Candidates.Select(c =>
            {
                var baseVal = (decimal)baseScores[c.Id];
                var fallbackEffective = rankingOptions.BaseWeight * baseVal + rankingOptions.AiWeight * fallbackAiVal;
                return c with
                {
                    TripMateBaseScore = baseVal,
                    EffectiveDesirabilityScore = fallbackEffective
                };
            }).ToList();

            var inputFallback = daNangScenario.Input with { Candidates = candidatesFallback };
            var weightedFallback = weightedInputsFactory.Create(
                candidatesFallback,
                profile,
                inputFallback,
                PoiRankingOutcomeCategory.Timeout);

            var serviceFallback = new ItineraryGenerationService(routeProvider, solverOptions);
            var resultFallback = await serviceFallback.GenerateAsync(inputFallback, weightedFallback, CancellationToken.None);
            resultFallback.IsSuccess.Should().BeTrue();
            var planFallback = resultFallback.Value;
            var fallbackVisits = planFallback.Items
                .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
                .OrderBy(i => i.SequenceNo)
                .ToList();

            var fallbackModelObj = planFallback.ModelObjective;
            var fallbackTravel = CalculateTravelMinutes(fallbackVisits);

            results.Add(new PersonaMeasurementResult(
                persona.Id,
                persona.DisplayName,
                baseStats,
                aiStats,
                successStats,
                fallbackStats,
                successVisits.Count,
                fallbackVisits.Count,
                successVisits.Select(v => v.PointOfInterestId!.Value).ToList(),
                fallbackVisits.Select(v => v.PointOfInterestId!.Value).ToList(),
                successModelObj,
                fallbackModelObj,
                successTravel,
                fallbackTravel));
        }

        var candidateNames = daNangScenario.Candidates.ToDictionary(c => c.Id, c => c.Name);
        var reportContent = RenderReport(results, candidateNames, rankingOptions);
        _output.WriteLine(reportContent);

        // Write report to docs/benchmarks directory
        var reportDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/benchmarks"));
        if (!Directory.Exists(reportDir))
        {
            Directory.CreateDirectory(reportDir);
        }
        var reportPath = Path.Combine(reportDir, "danang-score-distribution-1b.md");
        File.WriteAllText(reportPath, reportContent, Encoding.UTF8);

        // 4. One-directional hard gate: AI fallback must not reduce the itinerary by more than one stop
        foreach (var r in results)
        {
            AssertFallbackPreservesStops(r.DisplayName, r.SuccessStopsCount, r.FallbackStopsCount);
        }
    }

    internal static void AssertFallbackPreservesStops(string personaName, int successStops, int fallbackStops)
    {
        int reduction = successStops - fallbackStops;
        fallbackStops.Should().BeGreaterThanOrEqualTo(
            successStops - 1,
            $"AI fallback must not reduce the itinerary by more than one stop for persona '{personaName}'; " +
            $"success={successStops}, fallback={fallbackStops}, reduction={reduction}");
    }

    [Fact]
    public void HardGate_WhenFallbackLosesTwoOrMoreStops_Fails()
    {
        int successStops = 6;
        int fallbackStops = 4;

        Action assertAction = () => AssertFallbackPreservesStops("SyntheticPersona", successStops, fallbackStops);

        assertAction.Should().Throw<Exception>()
            .WithMessage("*must not reduce the itinerary by more than one stop*reduction=2*");
    }

    internal static string RenderReport(
        IReadOnlyList<PersonaMeasurementResult> results,
        IReadOnlyDictionary<long, string> candidateNames,
        PersonalizationRankingOptions? rankingOptions = null)
    {
        rankingOptions ??= new PersonalizationRankingOptions();
        var sb = new StringBuilder();
        sb.AppendLine("# Báo cáo Đo lường Phân phối Điểm Mục 1b (Đà Nẵng × 5 Personas)");
        sb.AppendLine();
        sb.AppendLine("## 1. Bảng Phân phối Điểm (BaseScore, AiScore, Base)");
        sb.AppendLine();
        sb.AppendLine("| Persona | Chỉ số | Min | Trung vị | Max | Mean | Độ lệch chuẩn (StdDev) |");
        sb.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var r in results)
        {
            sb.AppendLine(FormattableString.Invariant($"| **{r.DisplayName}** | **BaseScore** (nội bộ) | {r.BaseScoreStats.Min:F4} | {r.BaseScoreStats.Median:F4} | {r.BaseScoreStats.Max:F4} | {r.BaseScoreStats.Mean:F4} | {r.BaseScoreStats.StdDev:F4} |"));
            sb.AppendLine(FormattableString.Invariant($"| | **AiScore** (bge-m3) | {r.AiScoreStats.Min:F4} | {r.AiScoreStats.Median:F4} | {r.AiScoreStats.Max:F4} | {r.AiScoreStats.Mean:F4} | {r.AiScoreStats.StdDev:F4} |"));
            sb.AppendLine(FormattableString.Invariant($"| | **Base: AI Success** ({rankingOptions.BaseWeight:0.#} Base + {rankingOptions.AiWeight:0.#} Ai) | {r.BaseSuccessStats.Min:F4} | {r.BaseSuccessStats.Median:F4} | {r.BaseSuccessStats.Max:F4} | {r.BaseSuccessStats.Mean:F4} | {r.BaseSuccessStats.StdDev:F4} |"));
            sb.AppendLine(FormattableString.Invariant($"| | **Base: AI Fallback** ({rankingOptions.BaseWeight:0.#} Base + {rankingOptions.AiWeight:0.#} AiFallback) | {r.BaseFallbackStats.Min:F4} | {r.BaseFallbackStats.Median:F4} | {r.BaseFallbackStats.Max:F4} | {r.BaseFallbackStats.Mean:F4} | {r.BaseFallbackStats.StdDev:F4} |"));
        }

        sb.AppendLine();
        sb.AppendLine(FormattableString.Invariant($"## 2. So sánh Số điểm dừng và Lịch trình Weighted (AI Success vs. AI Fallback {rankingOptions.PersonalizedAiFallbackScore:F2}/{rankingOptions.DefaultAiFallbackScore:F2})"));
        sb.AppendLine();
        sb.AppendLine("| Persona | Số điểm (Success) | Số điểm (Fallback) | Chênh lệch ($\\Delta$) | Visited POIs (Success) | Visited POIs (Fallback) | Model Obj (Success) | Model Obj (Fallback) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var r in results)
        {
            var diff = r.SuccessStopsCount - r.FallbackStopsCount;
            var successPoiNames = string.Join(", ", r.SuccessVisitedPoiIds.Select(id => $"[{id}] {candidateNames[id]}"));
            var fallbackPoiNames = string.Join(", ", r.FallbackVisitedPoiIds.Select(id => $"[{id}] {candidateNames[id]}"));
            string diffStr = diff >= 0 ? $"+{diff}" : FormattableString.Invariant($"{diff}");
            sb.AppendLine(FormattableString.Invariant($"| **{r.DisplayName}** | **{r.SuccessStopsCount}** | **{r.FallbackStopsCount}** | {diffStr} | {successPoiNames} | {fallbackPoiNames} | {r.SuccessModelObj} | {r.FallbackModelObj} |"));
        }

        sb.AppendLine();
        sb.AppendLine("## 3. Nhận xét & Đánh giá Tiêu chí");
        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine(FormattableString.Invariant($"- **{r.DisplayName}**: StdDev AiScore = **{r.AiScoreStats.StdDev:F4}** {(r.AiScoreStats.StdDev < 0.05 ? "(⚠️ < 0.05: Cần xem xét chuẩn hóa)" : "(✅ >= 0.05: Phân tán tốt)")}."));
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    [Fact]
    public void RenderReport_UnderVietnameseCultureAndInvariantCulture_ProducesIdenticalOutput()
    {
        var sampleSummary = new DistributionSummary(0.1234, 0.4567, 0.8901, 0.5000, 0.1500);
        var sampleResults = new List<PersonaMeasurementResult>
        {
            new(
                "CultureEnthusiast",
                "Culture Enthusiast",
                sampleSummary,
                sampleSummary,
                sampleSummary,
                sampleSummary,
                6,
                6,
                [1L, 2L],
                [1L, 2L],
                30000,
                30000,
                45,
                45)
        };
        var sampleCandidateNames = new Dictionary<long, string>
        {
            [1L] = "Museum A",
            [2L] = "Bridge B"
        };

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
            CultureInfo.CurrentUICulture = new CultureInfo("vi-VN");
            string viReport = RenderReport(sampleResults, sampleCandidateNames);

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            string invariantReport = RenderReport(sampleResults, sampleCandidateNames);

            viReport.Should().Be(invariantReport);
            viReport.Should().Contain("0.1234");
            viReport.Should().Contain("0.5000");
            viReport.Should().NotContain("0,1234");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static DistributionSummary ComputeStats(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new DistributionSummary(0, 0, 0, 0, 0);

        var sorted = values.OrderBy(x => x).ToList();
        var min = sorted.First();
        var max = sorted.Last();
        var mean = sorted.Average();

        double median;
        int n = sorted.Count;
        if (n % 2 == 1)
        {
            median = sorted[n / 2];
        }
        else
        {
            median = (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }

        double sumSquares = sorted.Sum(x => Math.Pow(x - mean, 2));
        double stdDev = n > 1 ? Math.Sqrt(sumSquares / (n - 1)) : 0.0;

        return new DistributionSummary(min, median, max, mean, stdDev);
    }

    private static int CalculateTravelMinutes(IReadOnlyList<GeneratedItineraryItem> visitItems)
    {
        var travelMinutes = 0;
        for (int i = 0; i < visitItems.Count - 1; i++)
        {
            var leg = visitItems[i + 1].PlannedArrivalUtc - visitItems[i].PlannedDepartureUtc;
            if (leg.TotalMinutes > 0)
            {
                travelMinutes += (int)leg.TotalMinutes;
            }
        }
        return travelMinutes;
    }

    private static TravelerProfile? CreateProfile(PersonaDefinition persona)
    {
        if (persona.Id == "DefaultGeneralist") return null;

        var profile = TravelerProfile.CreateEmpty(1001, DateTimeOffset.UtcNow);
        profile.ReplacePreferences(
            interestTagsJson: JsonSerializer.Serialize(persona.Tags),
            preferredTransportMode: TransportMode.Motorbike,
            travelPace: persona.Pace,
            riskTolerance: persona.Risk,
            foodPreferencesJson: null,
            nowUtc: DateTimeOffset.UtcNow);
        return profile;
    }

    private static PersonalizationOptions CreatePersonalizationOptions()
    {
        return new PersonalizationOptions
        {
            Version = "2026.2",
            Pace = new Dictionary<TravelerPace, PaceParams>
            {
                [TravelerPace.Relaxed] = new(LambdaPerMin: 0.018, MaxStopsPerDay: 3, MaxDailyEffort: 200, RestAfterMin: 90),
                [TravelerPace.Moderate] = new(LambdaPerMin: 0.012, MaxStopsPerDay: 5, MaxDailyEffort: 300, RestAfterMin: 120),
                [TravelerPace.Fast] = new(LambdaPerMin: 0.008, MaxStopsPerDay: 8, MaxDailyEffort: 450, RestAfterMin: 180),
            },
            Mu = 0.005,
            GammaDefault = 0.7,
            GammaLoved = 0.9,
            RhoOutdoor = 0.25,
            RhoRest = 0.0,
            RhoStops = 0.30,
            RhoEffortPerHour = 0.05,
            TagToCategory = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["beach"] = [PoiCategoryNames.Beach],
                ["photography"] = [PoiCategoryNames.Culture],
                ["heritage"] = [PoiCategoryNames.Culture],
                ["museum"] = [PoiCategoryNames.Museum],
                ["nature"] = [PoiCategoryNames.Nature, PoiCategoryNames.NaturalAttraction],
                ["natural attraction"] = [PoiCategoryNames.NaturalAttraction],
                ["theme park"] = [PoiCategoryNames.ThemePark],
                ["water park"] = [PoiCategoryNames.WaterPark],
                ["food"] = [PoiCategoryNames.FoodAndDrink],
                ["coffee"] = [PoiCategoryNames.FoodAndDrink],
                ["cuisine"] = [PoiCategoryNames.FoodAndDrink],
                ["culinary"] = [PoiCategoryNames.FoodAndDrink],
                ["culture"] = [PoiCategoryNames.Culture],
                ["history"] = [PoiCategoryNames.Culture],
                ["art"] = [PoiCategoryNames.Culture],
                ["adventure"] = [PoiCategoryNames.NaturalAttraction],
                ["hiking"] = [PoiCategoryNames.NaturalAttraction],
                ["outdoor"] = [PoiCategoryNames.Nature, PoiCategoryNames.NaturalAttraction, PoiCategoryNames.Beach],
                ["sightseeing"] = [PoiCategoryNames.Culture],
                ["relaxation"] = [PoiCategoryNames.Beach],
            }
        };
    }

    private static GoldenScenario CreateDaNangRealScenario()
    {
        var poiDefs = new[]
        {
            (1L, "Bảo tàng Điêu khắc Chăm", 16.040393m, 108.223058m, PoiCategoryNames.Museum, 90, 60_000m, IndoorOutdoorType.Indoor, true, 8m, 8m, new[] { "culture", "museum", "history" }),
            (2L, "Bảo tàng Đà Nẵng", 16.071218m, 108.225226m, PoiCategoryNames.Museum, 90, 50_000m, IndoorOutdoorType.Indoor, true, 7m, 7m, new[] { "culture", "museum", "history" }),
            (3L, "Bảo tàng Mỹ thuật Đà Nẵng", 16.073538m, 108.219823m, PoiCategoryNames.Museum, 75, 20_000m, IndoorOutdoorType.Indoor, true, 8m, 9m, new[] { "culture", "photography", "art" }),
            (4L, "Bảo tàng Quân khu 5", 16.032273m, 108.216323m, PoiCategoryNames.Museum, 90, 60_000m, IndoorOutdoorType.Indoor, true, 6m, 6m, new[] { "history", "culture" }),
            (5L, "Cầu Rồng", 16.061158m, 108.230008m, PoiCategoryNames.Culture, 30, 0m, IndoorOutdoorType.Outdoor, false, 9m, 10m, new[] { "architecture", "photography", "sightseeing" }),
            (6L, "Cầu Sông Hàn", 16.072159m, 108.226790m, PoiCategoryNames.Culture, 30, 0m, IndoorOutdoorType.Outdoor, false, 8m, 9m, new[] { "architecture", "photography" }),
            (7L, "Cầu Tình Yêu", 16.062936m, 108.229821m, PoiCategoryNames.Culture, 30, 0m, IndoorOutdoorType.Outdoor, false, 8m, 9m, new[] { "photography", "relaxation" }),
            (8L, "Công viên APEC", 16.058101m, 108.223250m, PoiCategoryNames.Nature, 45, 0m, IndoorOutdoorType.Outdoor, false, 8m, 9m, new[] { "architecture", "relaxation", "photography" }),
            (9L, "Bãi biển Mỹ Khê", 16.069967m, 108.246066m, PoiCategoryNames.Beach, 120, 0m, IndoorOutdoorType.Outdoor, false, 9m, 9m, new[] { "beach", "relaxation", "nature", "outdoor" }),
            (10L, "Chợ Cồn", 16.068420m, 108.214113m, PoiCategoryNames.Culture, 60, 0m, IndoorOutdoorType.Indoor, true, 7m, 7m, new[] { "food", "coffee", "culture", "shopping" }),
            (11L, "Chùa Linh Ứng Bãi Bụt", 16.099685m, 108.277042m, PoiCategoryNames.Culture, 60, 0m, IndoorOutdoorType.Mixed, true, 10m, 10m, new[] { "heritage", "culture", "nature", "photography" }),
            (12L, "Ngũ Hành Sơn - Thủy Sơn", 16.003759m, 108.263876m, "Natural attraction", 120, 40_000m, IndoorOutdoorType.Outdoor, false, 9m, 9m, new[] { "nature", "adventure", "outdoor", "hiking" }),
            (13L, "Động Âm Phủ", 16.004488m, 108.264164m, "Natural attraction", 45, 20_000m, IndoorOutdoorType.Mixed, false, 7m, 6m, new[] { "nature", "adventure" }),
            (14L, "Làng đá mỹ nghệ Non Nước", 16.002829m, 108.261263m, PoiCategoryNames.Culture, 45, 0m, IndoorOutdoorType.Mixed, true, 7m, 7m, new[] { "heritage", "culture", "shopping" }),
            (15L, "Nhà hàng Madame Lân", 16.082500m, 108.223000m, PoiCategoryNames.FoodAndDrink, 60, 150_000m, IndoorOutdoorType.Indoor, true, 7m, 7m, new[] { "food", "cuisine", "coffee" }),
            (16L, "Cộng Cà Phê Bạch Đằng", 16.068000m, 108.225000m, PoiCategoryNames.FoodAndDrink, 45, 50_000m, IndoorOutdoorType.Indoor, true, 7m, 8m, new[] { "coffee", "food", "relaxation" })
        };

        var openingHours = new[] { new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(21, 0)) };

        var candidates = poiDefs.Select(p => new GenerationCandidate(
            Id: p.Item1,
            Name: p.Item2,
            Location: new RoutePoint(p.Item3, p.Item4),
            VisitDurationMinutes: p.Item6,
            EstimatedVisitCost: p.Item7,
            OpeningHours: openingHours,
            CategoryName: p.Item5,
            IndoorOutdoor: p.Item8,
            HasShelter: p.Item9,
            ScenicScore: p.Item10,
            PhotoRating: p.Item11,
            Tags: p.Item12)).ToList();

        var start = new RoutePoint(16.0610m, 108.2250m);
        var end = new RoutePoint(16.0610m, 108.2250m);

        var points = new List<RoutePoint> { start };
        points.AddRange(candidates.Select(c => c.Location));
        points.Add(end);

        int count = points.Count;
        var minutes = new int[count, count];
        var meters = new int[count, count];

        for (int i = 0; i < count; i++)
        {
            for (int j = 0; j < count; j++)
            {
                if (i == j)
                {
                    minutes[i, j] = 0;
                    meters[i, j] = 0;
                }
                else
                {
                    double distKm = HaversineKm(points[i], points[j]);
                    int m = (int)Math.Round(distKm * 1.4 * 1000.0);
                    int min = Math.Max(2, (int)Math.Ceiling(distKm * 1.4 / 30.0 * 60.0));
                    minutes[i, j] = min;
                    meters[i, j] = m;
                }
            }
        }

        var matrix = RouteDurationMatrix.Create(minutes, null, meters);

        var input = new GenerationInput(
            StartAtUtc: new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero),
            TimeZone: TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"),
            Start: start,
            End: end,
            AvailableMinutes: 480,
            TransportMode: TransportMode.Motorbike,
            RestPreference: RestPreference.None,
            BudgetVnd: 1_000_000m,
            Candidates: candidates,
            MandatoryPoiIds: [1L]);

        var (cands, idx) = Index(input);
        return new GoldenScenario("danang-real-1day", "RealDaNangSeed", true, input, matrix, cands, idx);
    }

    private static (IReadOnlyList<GenerationCandidate> Candidates, IReadOnlyDictionary<long, int> Index) Index(GenerationInput input)
    {
        var candidates = input.Candidates.OrderBy(c => c.Id).ToList();
        var index = candidates.Select((c, i) => (c.Id, Index: i)).ToDictionary(x => x.Id, x => x.Index);
        return (candidates, index);
    }

    private static double HaversineKm(RoutePoint a, RoutePoint b)
    {
        const double earthRadiusKm = 6371.0;
        double lat1 = (double)a.Latitude * Math.PI / 180.0;
        double lon1 = (double)a.Longitude * Math.PI / 180.0;
        double lat2 = (double)b.Latitude * Math.PI / 180.0;
        double lon2 = (double)b.Longitude * Math.PI / 180.0;

        double dLat = lat2 - lat1;
        double dLon = lon2 - lon1;

        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
        return earthRadiusKm * c;
    }

    private sealed class PrecomputedRouteDurationProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) =>
            Task.FromResult(matrix);
    }
}