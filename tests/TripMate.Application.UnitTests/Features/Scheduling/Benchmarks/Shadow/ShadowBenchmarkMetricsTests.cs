using System.Globalization;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public class ShadowBenchmarkMetricsTests
{
    [Fact]
    public void Create_UsesAuthoritativeTotalMatrixTravelMinutes()
    {
        var schedule = Schedule(totalMatrixTravelMinutes: 47, Visit(1, 8, 0, 9, 0));

        var result = ShadowBenchmarkMetrics.Create(
            "scenario", "persona", ObjectiveMode.WeightedUtility, schedule,
            Candidates(Candidate(1, "Culture")), TimeZoneInfo.Utc, [3, 1, 2]);

        result.TotalMatrixTravelMinutes.Should().Be(47);
    }

    [Fact]
    public void Create_CountsAuthoritativeDiversityGroups_NotCanonicalCategories()
    {
        var schedule = Schedule(12,
            Visit(1, 8, 0, 8, 30),
            Visit(2, 9, 0, 9, 30),
            Visit(3, 10, 0, 10, 30),
            Visit(4, 15, 0, 15, 30),
            Visit(5, 16, 0, 16, 30));

        var result = ShadowBenchmarkMetrics.Create(
            "scenario", "persona", ObjectiveMode.Lexicographic, schedule,
            Candidates(
                Candidate(1, "Culture"),
                Candidate(2, "Museum"),
                Candidate(3, "Nature"),
                Candidate(4, "Natural attraction"),
                Candidate(5, "Beach")),
            TimeZoneInfo.Utc,
            [1]);

        result.DiversityGroupCount.Should().Be(3);
    }

    [Fact]
    public void Create_CountsOnlyUnshelteredOutdoorVisitsOverlappingMidday()
    {
        var schedule = Schedule(18,
            Visit(1, 10, 50, 11, 10),
            Visit(2, 12, 0, 13, 0),
            Visit(3, 12, 0, 13, 0),
            Visit(4, 14, 30, 15, 0));

        var result = ShadowBenchmarkMetrics.Create(
            "scenario", "persona", ObjectiveMode.WeightedUtility, schedule,
            Candidates(
                Candidate(1, "Beach", IndoorOutdoorType.Outdoor, hasShelter: false),
                Candidate(2, "Museum", IndoorOutdoorType.Indoor, hasShelter: true),
                Candidate(3, "Nature", IndoorOutdoorType.Outdoor, hasShelter: true),
                Candidate(4, "Beach", IndoorOutdoorType.Outdoor, hasShelter: false)),
            TimeZoneInfo.Utc,
            [1]);

        result.MiddayOutdoorVisitCount.Should().Be(1);
        result.MiddayOutdoorVisitDenominator.Should().Be(4);
        result.MiddayOutdoorVisitRate.Should().Be(25.0);
    }

    [Theory]
    [InlineData(new double[0], 0.50, 0.0)]
    [InlineData(new[] { 7.0 }, 0.95, 7.0)]
    [InlineData(new[] { 5.0, 1.0, 3.0, 2.0, 4.0 }, 0.50, 3.0)]
    [InlineData(new[] { 4.0, 1.0, 3.0, 2.0 }, 0.50, 3.0)]
    [InlineData(new[] { 4.0, 1.0, 3.0, 2.0 }, 0.95, 4.0)]
    public void Percentile_UsesSolverComparisonConvention(double[] samples, double percentile, double expected)
    {
        ShadowBenchmarkMetrics.Percentile(samples, percentile).Should().Be(expected);
    }

    [Fact]
    public void Render_IsDeterministicInvariantAndUsesLfInStableOrder()
    {
        var metadata = new ShadowBenchmarkReportMetadata(
            "2026-10-21", "Release", "Test OS", ".NET Test", "Test CPU", 8, WarmupIterations: 1);
        ShadowBenchmarkRunResult[] results =
        [
            Result("z-scenario", "b-persona", ObjectiveMode.WeightedUtility, 12.5, 19.75),
            Result("a-scenario", "a-persona", ObjectiveMode.Lexicographic, 1.25, 2.5)
        ];

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
            var vi = ShadowBenchmarkMarkdownRenderer.Render(metadata, results);

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = ShadowBenchmarkMarkdownRenderer.Render(metadata, results);

            vi.Should().Be(invariant);
            vi.Should().NotContain("\r");
            vi.Should().Contain("1.25").And.NotContain("1,25");
            vi.IndexOf("a-scenario", StringComparison.Ordinal)
                .Should().BeLessThan(vi.IndexOf("z-scenario", StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Render_IncludesSeedExtensionAttribution()
    {
        var metadata = new ShadowBenchmarkReportMetadata(
            "2026-10-21", "Release", "Test OS", ".NET Test", "Test CPU", 8, WarmupIterations: 1);
        var comparison = new ShadowBenchmarkSeedComparison(
            "CultureEnthusiast",
            ObjectiveMode.WeightedUtility,
            BaselineStopCount: 6,
            V3bStopCount: 7,
            BaselineTravelMinutes: 31,
            V3bTravelMinutes: 42,
            BaselineDiversityGroupCount: 1,
            V3bDiversityGroupCount: 2,
            BaselineMiddayOutdoorCount: 0,
            V3bMiddayOutdoorCount: 1,
            BaselineModelObjective: 26841,
            V3bModelObjective: 27100,
            BaselineVisitedPoiIds: [5, 7, 1],
            V3bVisitedPoiIds: [5, 17, 7, 1]);

        var markdown = ShadowBenchmarkMarkdownRenderer.Render(metadata, [], [comparison]);

        markdown.Should().Contain("| CultureEnthusiast | WeightedUtility | 6 -> 7 | 31 -> 42 | 1 -> 2 | 0 -> 1 | 26841 -> 27100 | 5,7,1 -> 5,17,7,1 |");
    }

    private static ShadowBenchmarkRunResult Result(
        string scenarioId,
        string personaId,
        ObjectiveMode mode,
        double p50,
        double p95) => new(
            scenarioId,
            personaId,
            mode,
            Feasible: true,
            StopCount: 2,
            TotalMatrixTravelMinutes: 17,
            DiversityGroupCount: 2,
            MiddayOutdoorVisitCount: 1,
            MiddayOutdoorVisitDenominator: 2,
            MiddayOutdoorVisitRate: 50,
            ModelObjective: 1234,
            VisitedPoiIds: [1, 2],
            DurationSamplesMs: [p50, p95],
            P50Milliseconds: p50,
            P95Milliseconds: p95);

    private static EvaluatedItinerarySchedule Schedule(int totalMatrixTravelMinutes, params GeneratedItineraryItem[] items)
    {
        var plan = new GeneratedItineraryPlan(items, Utc(18, 0), 600, 0, 4321);
        return new EvaluatedItinerarySchedule(
            plan,
            totalMatrixTravelMinutes,
            plan.TotalDurationMinutes,
            plan.EndAtUtc,
            plan.TotalEstimatedCost,
            items.Select(item => item.PointOfInterestId!.Value).ToArray());
    }

    private static GeneratedItineraryItem Visit(long id, int arrivalHour, int arrivalMinute, int departureHour, int departureMinute) =>
        new(
            (int)id,
            id,
            $"POI {id}",
            ItineraryItemKind.Visit,
            Utc(arrivalHour, arrivalMinute),
            Utc(departureHour, departureMinute),
            false,
            0,
            string.Empty);

    private static GenerationCandidate Candidate(
        long id,
        string category,
        IndoorOutdoorType indoorOutdoor = IndoorOutdoorType.Outdoor,
        bool hasShelter = false) =>
        new(id, $"POI {id}", new RoutePoint(16m, 108m), 30, 0, [], CategoryName: category,
            HasShelter: hasShelter, IndoorOutdoor: indoorOutdoor);

    private static IReadOnlyDictionary<long, GenerationCandidate> Candidates(params GenerationCandidate[] candidates) =>
        candidates.ToDictionary(candidate => candidate.Id);

    private static DateTimeOffset Utc(int hour, int minute) =>
        new(2026, 10, 21, hour, minute, 0, TimeSpan.Zero);
}