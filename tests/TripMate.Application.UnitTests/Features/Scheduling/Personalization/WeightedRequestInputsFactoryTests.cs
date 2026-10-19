using FluentAssertions;
using Microsoft.Extensions.Options;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

public class WeightedRequestInputsFactoryTests
{
    private static readonly SchedulingGenerationOptions SchedulingOptions = new();

    private static PersonalParamsFactory CreateParamsFactory()
    {
        var options = Options.Create(new PersonalizationOptions());
        return new PersonalParamsFactory(options);
    }

    private static GenerationInput CreateDaNangGenerationInput(IReadOnlyList<GenerationCandidate> candidates)
    {
        var daNangCoord = new RoutePoint(16.0544m, 108.2022m);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        var startUtc = new DateTimeOffset(2026, 6, 15, 1, 0, 0, TimeSpan.Zero); // 08:00 local (+7)

        return new GenerationInput(
            StartAtUtc: startUtc,
            TimeZone: timeZone,
            Start: daNangCoord,
            End: daNangCoord,
            AvailableMinutes: 480,
            TransportMode: TransportMode.Car,
            RestPreference: RestPreference.Auto,
            BudgetVnd: null,
            Candidates: candidates,
            MandatoryPoiIds: []);
    }

    private static GenerationCandidate CreateCandidate(
        long id,
        string name = "Test POI",
        decimal? tripMateBase = null,
        decimal? effectiveDesirability = null,
        string category = "Culture",
        bool isRest = false)
    {
        return new GenerationCandidate(
            Id: id,
            Name: name,
            Location: new RoutePoint(16.0544m, 108.2022m),
            VisitDurationMinutes: 60,
            EstimatedVisitCost: 100_000,
            OpeningHours: [],
            TripMateBaseScore: tripMateBase,
            EffectiveDesirabilityScore: effectiveDesirability,
            ScenicScoreForRanking: 8m,
            PhotoRatingForRanking: 8m,
            EstimatedVisitCostForRanking: 100_000,
            PreferenceScore: 100,
            ScenicScore: 8m,
            PhotoRating: 8m,
            CategoryName: isRest ? PoiCategoryNames.FoodAndDrink : category,
            HasShelter: true);
    }

    [Fact]
    public void Create_CandidateWithEffectiveDesirabilityScore_UsesEffectiveDesirability()
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        var c1 = CreateCandidate(1L, effectiveDesirability: 0.85m, tripMateBase: 0.60m);
        var input = CreateDaNangGenerationInput([c1]);

        var result = factory.Create([c1], profile: null, input);

        result.BaseByPoiId.Should().ContainKey(1L);
        result.BaseByPoiId[1L].Should().BeApproximately(0.85, 1e-4);
    }

    [Fact]
    public void Create_CandidateWithOnlyTripMateBaseScore_UsesTripMateBaseScore()
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        // EffectiveDesirabilityScore = null, TripMateBaseScore = 0.72m
        var c1 = CreateCandidate(1L, effectiveDesirability: null, tripMateBase: 0.72m);
        var input = CreateDaNangGenerationInput([c1]);

        var result = factory.Create([c1], profile: null, input);

        result.BaseByPoiId.Should().ContainKey(1L);
        result.BaseByPoiId[1L].Should().BeApproximately(0.72, 1e-4);
    }

    [Fact]
    public void Create_CandidateWithNeitherScore_DefaultsToHalf()
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        // Both are null
        var c1 = CreateCandidate(1L, effectiveDesirability: null, tripMateBase: null);
        var input = CreateDaNangGenerationInput([c1]);

        var result = factory.Create([c1], profile: null, input);

        result.BaseByPoiId.Should().ContainKey(1L);
        result.BaseByPoiId[1L].Should().Be(0.5);
    }

    [Fact]
    public void Create_CandidateWithZeroEffectiveDesirability_PreservesZeroScore()
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        // EffectiveDesirability is 0m (explicitly scored 0.0), should NOT default to 0.5
        var c1 = CreateCandidate(1L, effectiveDesirability: 0m, tripMateBase: 0.72m);
        var input = CreateDaNangGenerationInput([c1]);

        var result = factory.Create([c1], profile: null, input);

        result.BaseByPoiId.Should().ContainKey(1L);
        result.BaseByPoiId[1L].Should().Be(0.0);
    }

    [Fact]
    public void Create_MandatoryCandidate_UsesTripMateBaseScore_WhenPresent()
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        var c1 = CreateCandidate(101L, effectiveDesirability: null, tripMateBase: 0.65m);
        var input = CreateDaNangGenerationInput([c1]) with { MandatoryPoiIds = [101L] };

        var result = factory.Create([c1], profile: null, input);

        result.BaseByPoiId[101L].Should().BeApproximately(0.65, 1e-4);
    }

    [Fact]
    public void Create_MandatoryCandidate_DefaultsToHalf_WhenNoScoresPresent()
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        var c1 = CreateCandidate(102L, effectiveDesirability: null, tripMateBase: null);
        var input = CreateDaNangGenerationInput([c1]) with { MandatoryPoiIds = [102L] };

        var result = factory.Create([c1], profile: null, input);

        result.BaseByPoiId[102L].Should().Be(0.5);
    }

    [Fact]
    public void Create_EnsuresAllNonRestCandidatesHaveBaseScoreInZeroToOneRange()
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        var candidates = new List<GenerationCandidate>
        {
            CreateCandidate(1L, effectiveDesirability: 0.9m, tripMateBase: 0.8m),
            CreateCandidate(2L, effectiveDesirability: null, tripMateBase: 0.4m),
            CreateCandidate(3L, effectiveDesirability: null, tripMateBase: null),
            CreateCandidate(4L, effectiveDesirability: 0.3m, tripMateBase: 0.3m, isRest: true),
        };
        var input = CreateDaNangGenerationInput(candidates);

        var result = factory.Create(candidates, profile: null, input);

        foreach (var candidate in candidates)
        {
            result.BaseByPoiId.Should().ContainKey(candidate.Id);
            var score = result.BaseByPoiId[candidate.Id];
            score.Should().BeInRange(0.0, 1.0);
        }
    }

    [Theory]
    [InlineData(PoiRankingOutcomeCategory.Success, "ai-blend")]
    [InlineData(PoiRankingOutcomeCategory.ProviderSkipped, "base-only")]
    [InlineData(PoiRankingOutcomeCategory.Timeout, "base-fallback:timeout")]
    [InlineData(PoiRankingOutcomeCategory.ServerError, "base-fallback:server-error")]
    [InlineData(PoiRankingOutcomeCategory.InvalidResponse, "base-fallback:invalid-response")]
    [InlineData(PoiRankingOutcomeCategory.Quota, "base-fallback:quota")]
    [InlineData(PoiRankingOutcomeCategory.Network, "base-fallback:network")]
    public void Create_WithVariousRankingOutcomes_SetsCorrectBaseSourceWithoutThrowing(
        PoiRankingOutcomeCategory outcome,
        string expectedBaseSource)
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        var c1 = CreateCandidate(1L, effectiveDesirability: 0.7m, tripMateBase: 0.7m);
        var input = CreateDaNangGenerationInput([c1]);

        var result = factory.Create([c1], profile: null, input, rankingOutcome: outcome);

        result.BaseSource.Should().Be(expectedBaseSource);
        result.BaseByPoiId[1L].Should().BeApproximately(0.7, 1e-4);
    }

    [Fact]
    public void Create_InfersAiBlend_WhenOutcomeNotProvided_AndCandidateBlended()
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        var c1 = CreateCandidate(1L, effectiveDesirability: 0.85m, tripMateBase: 0.60m);
        var input = CreateDaNangGenerationInput([c1]);

        var result = factory.Create([c1], profile: null, input, rankingOutcome: null);

        result.BaseSource.Should().Be("ai-blend");
    }

    [Fact]
    public void Create_InfersBaseOnly_WhenOutcomeNotProvided_AndNoAiBlend()
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        var c1 = CreateCandidate(1L, effectiveDesirability: 0.60m, tripMateBase: 0.60m);
        var input = CreateDaNangGenerationInput([c1]);

        var result = factory.Create([c1], profile: null, input, rankingOutcome: null);

        result.BaseSource.Should().Be("base-only");
    }

    [Fact]
    public void Create_TimeFitIntegration_MatchesSunServiceForDaNang()
    {
        var factory = new WeightedRequestInputsFactory(CreateParamsFactory());
        var c1 = CreateCandidate(1L, effectiveDesirability: 0.8m, tripMateBase: 0.8m);
        var input = CreateDaNangGenerationInput([c1]);

        var result = factory.Create([c1], profile: null, input);

        var matrix = RouteDurationMatrix.Create(new int[3, 3]);
        var ctx = CspItinerarySolver.CreatePrimaryContext(
            input,
            matrix,
            [c1],
            new Dictionary<long, int> { [1L] = 1 },
            SchedulingOptions);

        var timeFitProvider = result.TimeFit.Create(ctx);
        timeFitProvider.Should().NotBeNull();
        timeFitProvider.Should().BeOfType<RealTimeFitProvider>();

        // Kiểm tra đối chiếu với SunService tại Đà Nẵng (16.0544, 108.2022) ngày 2026-06-15
        var dateOnly = new DateOnly(2026, 6, 15);
        var (sunrise, sunset) = SunService.Instance.Get(16.0544, 108.2022, dateOnly, input.TimeZone);
        sunrise.Hour.Should().Be(5); // Mặt trời mọc ở Đà Nẵng vào tháng 6 khoảng 05:15
        sunset.Hour.Should().Be(18); // Mặt trời lặn khoảng 18:20
    }

    [Fact]
    public void Solver_SensitivityTest_BaseScoreDirectlyDrivesSelection()
    {
        // 1. Lấy kịch bản nhỏ 5 ứng viên tùy chọn từ WeightedScenarioFactory
        var scenarios = WeightedScenarioFactory.CreateAllScenarios();
        var scenario = scenarios.First(s => s.Id == "weighted-small-c5-m0-none-s52");

        var evaluator = new ItineraryScheduleEvaluator(SchedulingOptions);
        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            TimeLimitMilliseconds = 10_000,
            PolishWithLocalSearch = false
        };

        // 2. Chạy baseline với điểm Base đồng đều 0.5 cho tất cả ứng viên
        var baseScoresUniform = scenario.Candidates.ToDictionary(c => c.Id, _ => 0.5);
        var requestUniform = new WeightedRequestInputs(
            baseScoresUniform,
            new PersonalParams(0.015, 0.005, 5, 300, 120, 3, 0.6, 0.6, 0.85, new HashSet<string>(), 0.25, 0.20, 0.30, 0.05, "test"),
            new ConstantTimeFitFactory());

        var solver = new CspItinerarySolver(evaluator, SchedulingOptions, cspOptions);
        var baselineResult = solver.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            initialIncumbent: null, weighted: requestUniform);

        baselineResult.Schedule.Should().NotBeNull();
        var baselineSelectedIds = baselineResult.Schedule!.VisitPoiIds.ToHashSet();
        baselineSelectedIds.Should().NotBeEmpty("Kịch bản khả thi phải chọn ít nhất một điểm.");

        var unselectedCandidate = scenario.Candidates.FirstOrDefault(c => !baselineSelectedIds.Contains(c.Id));
        unselectedCandidate.Should().NotBeNull("Kịch bản có 5 ứng viên phải có ít nhất một điểm không được chọn.");

        var selectedCandidate = scenario.Candidates.First(c => baselineSelectedIds.Contains(c.Id));

        // 3. THỬ NGHIỆM 1: Nâng Base của unselectedCandidate lên 1.0 (các điểm khác hạ xuống 0.1)
        var boostedBaseScores = scenario.Candidates.ToDictionary(
            c => c.Id,
            c => c.Id == unselectedCandidate!.Id ? 1.0 : 0.1);

        var requestBoosted = new WeightedRequestInputs(
            boostedBaseScores,
            requestUniform.Params,
            new ConstantTimeFitFactory());

        var boostedResult = solver.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            initialIncumbent: null, weighted: requestBoosted);

        var boostedSelectedIds = boostedResult.Schedule!.VisitPoiIds.ToHashSet();
        boostedSelectedIds.Should().Contain(
            unselectedCandidate!.Id,
            "Khi nâng Base lên 1.0, điểm trước đó không được chọn phải xuất hiện trong lịch trình.");

        // 4. THỬ NGHIỆM 2: Hạ Base của selectedCandidate xuống 0.0 (các điểm khác giữ 0.9)
        var suppressedBaseScores = scenario.Candidates.ToDictionary(
            c => c.Id,
            c => c.Id == selectedCandidate.Id ? 0.0 : 0.9);

        var requestSuppressed = new WeightedRequestInputs(
            suppressedBaseScores,
            requestUniform.Params,
            new ConstantTimeFitFactory());

        var suppressedResult = solver.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            initialIncumbent: null, weighted: requestSuppressed);

        var suppressedSelectedIds = suppressedResult.Schedule!.VisitPoiIds.ToHashSet();
        suppressedSelectedIds.Should().NotContain(
            selectedCandidate.Id,
            "Khi hạ Base xuống 0.0, điểm trước đó được chọn phải biến mất khỏi lịch trình.");
    }
}
