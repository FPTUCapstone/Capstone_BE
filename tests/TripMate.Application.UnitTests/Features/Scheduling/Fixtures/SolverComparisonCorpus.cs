using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Fixtures;

/// <summary>
/// Danh tính của một scenario trong corpus benchmark. <see cref="CandidateCount"/> và <see cref="Seed"/> là null với
/// fixture có tên.
/// </summary>
public sealed record SolverComparisonScenarioDescriptor(
    string Segment,
    string Name,
    RestPreference RestPreference,
    int MandatoryCount,
    int? CandidateCount,
    int? Seed);

/// <summary>
/// Hợp đồng corpus của <c>tools/benchmarks/solver-comparison</c>: runner sinh scenario từ đây, và
/// <see cref="SolverComparisonReport.Validate"/> dựng lại đúng danh sách scenario mong đợi từ metadata cũng từ đây.
/// Nhờ vậy metadata không thể mô tả một corpus khác với dữ liệu mà vẫn qua kiểm tra.
/// </summary>
public static class SolverComparisonCorpus
{
    public const string NamedSegment = "named";

    public static readonly IReadOnlyList<int> DefaultCandidateCounts = [10, 20, 40];

    public static readonly IReadOnlyList<int> DefaultMandatoryCounts = [0, 1, 3, 6];

    public static readonly IReadOnlyList<RestPreference> DefaultRestPreferences =
        [RestPreference.None, RestPreference.Auto, RestPreference.Frequent];

    public const int DefaultSeeds = 25;

    /// <summary>Các fixture có tên, theo thứ tự chạy.</summary>
    public static readonly IReadOnlyList<(string Name, Func<(GenerationInput Input, RouteDurationMatrix Matrix)> Create)> NamedFixtures =
    [
        ("zigzag", () => OptionalRouteOptimizationScenarios.CreateZigzagScenario()),
        ("zigzag-240", () => OptionalRouteOptimizationScenarios.CreateZigzagScenario(240)),
        ("tail-infeasible", OptionalRouteOptimizationScenarios.CreateTailInfeasibleMiddleFeasibleScenario),
        ("asymmetric", OptionalRouteOptimizationScenarios.CreateAsymmetricScenario),
        ("two-opt", OptionalRouteOptimizationScenarios.CreateTwoOptStrictImprovementScenario),
        ("relocate", OptionalRouteOptimizationScenarios.CreateRelocateStrictImprovementScenario),
        ("reconsideration", OptionalRouteOptimizationScenarios.CreateReconsiderationScenario),
    ];

    public static string Segment(int candidateCount, int mandatoryCount, RestPreference restPreference) =>
        $"c{candidateCount}-m{mandatoryCount}"
        + (restPreference == RestPreference.None ? string.Empty : "-" + restPreference.ToString().ToLowerInvariant());

    public static string Name(string segment, int seed) => $"{segment}-s{seed}";

    /// <summary>
    /// Mọi scenario của corpus, theo thứ tự chạy: các fixture có tên (khi <paramref name="includeNamedFixtures"/>), rồi
    /// kiểu nghỉ × số ứng viên × số điểm bắt buộc × seed 1..<paramref name="seeds"/>.
    /// </summary>
    public static IReadOnlyList<SolverComparisonScenarioDescriptor> Describe(
        IReadOnlyList<int> candidateCounts,
        IReadOnlyList<int> mandatoryCounts,
        IReadOnlyList<RestPreference> restPreferences,
        int seeds,
        bool includeNamedFixtures)
    {
        var descriptors = new List<SolverComparisonScenarioDescriptor>();
        if (includeNamedFixtures)
        {
            foreach (var (name, create) in NamedFixtures)
            {
                var (input, _) = create();
                descriptors.Add(new SolverComparisonScenarioDescriptor(
                    NamedSegment, name, input.RestPreference, input.MandatoryPoiIds.Count, null, null));
            }
        }

        foreach (var restPreference in restPreferences)
        {
            foreach (var candidates in candidateCounts)
            {
                foreach (var mandatory in mandatoryCounts)
                {
                    var segment = Segment(candidates, mandatory, restPreference);
                    for (var seed = 1; seed <= seeds; seed++)
                    {
                        descriptors.Add(new SolverComparisonScenarioDescriptor(
                            segment, Name(segment, seed), restPreference, mandatory, candidates, seed));
                    }
                }
            }
        }

        return descriptors;
    }

    /// <summary>Dựng đầu vào và ma trận của một scenario.</summary>
    public static (GenerationInput Input, RouteDurationMatrix Matrix) Create(SolverComparisonScenarioDescriptor descriptor)
    {
        if (descriptor.CandidateCount is { } candidates && descriptor.Seed is { } seed)
        {
            return OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
                candidates, descriptor.MandatoryCount, seed, descriptor.RestPreference);
        }

        return NamedFixtures.Single(fixture => fixture.Name == descriptor.Name).Create();
    }
}