using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

public static class WeightedScenarioFactory
{
    private static (IReadOnlyList<GenerationCandidate> Candidates, IReadOnlyDictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.OrderBy(c => c.Id).ToList();
        var indices = candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        return (candidates, indices);
    }

    public static IReadOnlyList<GoldenScenario> CreateAllScenarios()
    {
        var list = new List<GoldenScenario>(GoldenScenarioFactory.CreateAllScenarios());

        // =========================================================================
        // Nhóm G: Kịch bản nhỏ duyệt toàn bộ không gian (SearchCompleted = true)
        // Bổ sung cho Weighted Utility để đạt ngưỡng >= 20 kịch bản hoàn tất duyệt cây
        // =========================================================================
        (int C, int M)[] smallConfigs =
        [
            (4, 0), (4, 1),
            (5, 0), (5, 1), (5, 2),
            (6, 0), (6, 1), (6, 2), (6, 3),
            (7, 2)
        ];

        var seed = 50;
        foreach (var (c, m) in smallConfigs)
        {
            var id = $"weighted-small-c{c}-m{m}-none-s{seed}";
            var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(c, m, seed++, RestPreference.None);
            var (cands, idx) = Index(input);
            list.Add(new GoldenScenario(id, "GroupG_SmallExhaustive", true, input, matrix, cands, idx));
        }

        // Kịch bản horizon hẹp (120 phút)
        {
            var (inputTight, matrixTight) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(6, 1, 61, RestPreference.None);
            inputTight = inputTight with { AvailableMinutes = 120 };
            var (candsTight, idxTight) = Index(inputTight);
            list.Add(new GoldenScenario("weighted-small-tight-horizon-s61", "GroupG_SmallExhaustive", true, inputTight, matrixTight, candsTight, idxTight));
        }

        // Kịch bản MaxStops = 3 với 8 ứng viên
        {
            var (inputStops, matrixStops) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(8, 0, 62, RestPreference.None);
            var (candsStops, idxStops) = Index(inputStops);
            list.Add(new GoldenScenario("weighted-small-maxstops-3-s62", "GroupG_SmallExhaustive", true, inputStops, matrixStops, candsStops, idxStops,
                CspOptions: new CspOptions { MaxStops = 3, TimeLimitMilliseconds = 120_000 }));
        }

        return list;
    }
}
