using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden;

public sealed record GoldenScenario(
    string Id,
    string Source,
    bool ExpectedFeasible,
    GenerationInput Input,
    RouteDurationMatrix Matrix,
    IReadOnlyList<GenerationCandidate> Candidates,
    IReadOnlyDictionary<long, int> Indices,
    EvaluatedItinerarySchedule? InitialIncumbent = null,
    CspOptions? CspOptions = null)
{
    public override string ToString() => Id;
}

public static class GoldenScenarioFactory
{
    private static readonly SchedulingGenerationOptions DefaultOptions = new();

    public static IReadOnlyList<GoldenScenario> CreateAllScenarios(SchedulingGenerationOptions? options = null)
    {
        var opts = options ?? DefaultOptions;
        var list = new List<GoldenScenario>(55);

        // =========================================================================
        // Nhóm A: Lưới Corpus chuẩn (36 kịch bản)
        // 3 số lượng ứng viên (10, 20, 40) x 4 số điểm bắt buộc (0, 1, 3, 6) x 3 kiểu nghỉ (None, Auto, Frequent)
        // =========================================================================
        int[] candidateCounts = [10, 20, 40];
        int[] mandatoryCounts = [0, 1, 3, 6];
        RestPreference[] restPreferences = [RestPreference.None, RestPreference.Auto, RestPreference.Frequent];

        foreach (var rest in restPreferences)
        {
            var restStr = rest switch
            {
                RestPreference.None => "none",
                RestPreference.Auto => "auto",
                RestPreference.Frequent => "frequent",
                _ => rest.ToString().ToLowerInvariant(),
            };

            foreach (var cand in candidateCounts)
            {
                foreach (var mand in mandatoryCounts)
                {
                    var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
                        candidateCount: cand,
                        mandatoryCount: mand,
                        seed: 1,
                        restPreference: rest);

                    var (candidates, indices) = Index(input);
                    var id = $"grid-c{cand}-m{mand}-{restStr}-s1";
                    list.Add(new GoldenScenario(id, "GroupA_Grid", true, input, matrix, candidates, indices));
                }
            }
        }

        // =========================================================================
        // Nhóm B: Bẫy định tuyến & Named Fixtures (7 kịch bản)
        // =========================================================================
        foreach (var (name, create) in SolverComparisonCorpus.NamedFixtures)
        {
            var (input, matrix) = create();
            var (candidates, indices) = Index(input);
            var id = $"named-{name}";
            var expectedFeasible = name != "tail-infeasible";
            list.Add(new GoldenScenario(id, "GroupB_Named", expectedFeasible, input, matrix, candidates, indices));
        }

        // =========================================================================
        // Nhóm C: Incumbent ban đầu (4 kịch bản)
        // 3 kịch bản kèm lịch heuristic hợp lệ (warm-start) + 1 kịch bản kèm lịch không hợp lệ (kiểm tra từ chối)
        // =========================================================================
        {
            // C1: None - Cung cấp incumbent tối ưu sẵn để kích hoạt ReturnedInitialIncumbent == true
            var (inputC1, matrixC1) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, seed: 10, RestPreference.None);
            var (candsC1, idxC1) = Index(inputC1);
            var optimalResult = new CspItinerarySolver(new ItineraryScheduleEvaluator(opts), opts).Solve(inputC1, matrixC1, candsC1, idxC1);
            var optimalIncumbent = optimalResult.Schedule!;
            list.Add(new GoldenScenario("incumbent-valid-c20-m2-none-s10", "GroupC_Incumbent", true, inputC1, matrixC1, candsC1, idxC1, optimalIncumbent));

            // C2: Auto
            var (inputC2, matrixC2) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 1, seed: 11, RestPreference.Auto);
            var (candsC2, idxC2) = Index(inputC2);
            var heurC2 = HeuristicSchedule(inputC2, matrixC2, candsC2, idxC2, opts);
            list.Add(new GoldenScenario("incumbent-valid-c20-m1-auto-s11", "GroupC_Incumbent", true, inputC2, matrixC2, candsC2, idxC2, heurC2));

            // C3: Frequent
            var (inputC3, matrixC3) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(40, 3, seed: 12, RestPreference.Frequent);
            var (candsC3, idxC3) = Index(inputC3);
            var heurC3 = HeuristicSchedule(inputC3, matrixC3, candsC3, idxC3, opts);
            list.Add(new GoldenScenario("incumbent-valid-c40-m3-frequent-s12", "GroupC_Incumbent", true, inputC3, matrixC3, candsC3, idxC3, heurC3));

            // C4: Invalid incumbent (chứa POI không có trong tập ứng viên -> solver phải từ chối và chạy từ đầu)
            var (inputC4, matrixC4) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 1, seed: 13, RestPreference.None);
            var (candsC4, idxC4) = Index(inputC4);
            var invalidPlan = new GeneratedItineraryPlan([], inputC4.StartAtUtc.AddMinutes(120), 120, 0m);
            var invalidIncumbent = new EvaluatedItinerarySchedule(invalidPlan, 10, 120, inputC4.StartAtUtc.AddMinutes(120), 0m, [999999L]);
            list.Add(new GoldenScenario("incumbent-invalid-rejected-s13", "GroupC_Incumbent", true, inputC4, matrixC4, candsC4, idxC4, invalidIncumbent));
        }

        // =========================================================================
        // Nhóm D: Ngân sách chặt & Cắt tỉa nhánh Budget (3 kịch bản)
        // =========================================================================
        {
            // D1: Ngân sách vừa đúng cho 2 điểm bắt buộc (mỗi điểm 15,000 VND -> 30,000 VND)
            var (inputD1, matrixD1) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, seed: 21, RestPreference.None);
            inputD1 = inputD1 with { BudgetVnd = 30_000m };
            var (candsD1, idxD1) = Index(inputD1);
            list.Add(new GoldenScenario("budget-tight-exact-mandatory-s21", "GroupD_Budget", true, inputD1, matrixD1, candsD1, idxD1));

            // D2: Ngân sách 15,000 VND nhỏ hơn tổng chi phí 2 điểm bắt buộc (30,000 VND) -> Proved Infeasible
            var (inputD2, matrixD2) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, seed: 22, RestPreference.None);
            inputD2 = inputD2 with { BudgetVnd = 15_000m };
            var (candsD2, idxD2) = Index(inputD2);
            list.Add(new GoldenScenario("budget-infeasible-pruned-s22", "GroupD_Budget", false, inputD2, matrixD2, candsD2, idxD2));

            // D3: Không có điểm bắt buộc, nhưng ngân sách hạn chế chỉ cho phép tối đa 3 điểm tùy chọn
            var (inputD3, matrixD3) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(40, 0, seed: 23, RestPreference.Auto);
            inputD3 = inputD3 with { BudgetVnd = 45_000m };
            var (candsD3, idxD3) = Index(inputD3);
            list.Add(new GoldenScenario("budget-limited-optional-s23", "GroupD_Budget", true, inputD3, matrixD3, candsD3, idxD3));
        }

        // =========================================================================
        // Nhóm E: Chạm giới hạn Tie / Đánh giá nhiều hoán vị (3 kịch bản)
        // =========================================================================
        {
            // E1: 7 điểm bắt buộc với ma trận đối xứng (chặng 5 phút) dưới Frequent rest
            // Sinh ra > 4096 hoán vị có cùng inclusion và travel time -> kích hoạt TieEvaluationLimitReached (4,096)
            var candidatesE1 = Enumerable.Range(1, 7).Select(i => OptionalRouteOptimizationScenarios.CreateCandidate(
                id: i,
                name: $"POI_{i}",
                visitDurationMinutes: 30,
                cost: 10_000m,
                effectiveDesirabilityScore: 0.9m
            )).ToArray();

            var inputE1 = OptionalRouteOptimizationScenarios.CreateInput(
                availableMinutes: 600,
                candidates: candidatesE1,
                mandatoryPoiIds: candidatesE1.Select(c => c.Id).ToArray(),
                restPreference: RestPreference.Frequent,
                budgetVnd: 200_000m);

            var totalPointsE1 = candidatesE1.Length + 2;
            var matrixDataE1 = new int[totalPointsE1, totalPointsE1];
            for (var i = 0; i < totalPointsE1; i++)
            {
                for (var j = 0; j < totalPointsE1; j++)
                {
                    matrixDataE1[i, j] = i == j ? 0 : 5;
                }
            }
            var matrixE1 = RouteDurationMatrix.Create(matrixDataE1);
            var (candsE1, idxE1) = Index(inputE1);
            list.Add(new GoldenScenario("tie-c20-m0-frequent-s31", "GroupE_Tie", true, inputE1, matrixE1, candsE1, idxE1,
                CspOptions: new CspOptions { MaxNodes = 50_000, TimeLimitMilliseconds = 120_000 }));

            // E2: 14 ứng viên tùy chọn, mỗi lần tìm được lời giải trên mô hình đều tốt hơn incumbent cũ nhưng hỏng khi chèn nghỉ ở Evaluator
            // -> kích hoạt InSearchEvaluationLimitReached (16,384)
            var candidatesE2 = Enumerable.Range(1, 14).Select(i => OptionalRouteOptimizationScenarios.CreateCandidate(
                id: i,
                name: $"POI_{i}",
                visitDurationMinutes: 25,
                cost: 10_000m,
                effectiveDesirabilityScore: 0.99m - i * 0.01m
            )).ToArray();

            var inputE2 = OptionalRouteOptimizationScenarios.CreateInput(
                availableMinutes: 300,
                candidates: candidatesE2,
                mandatoryPoiIds: [],
                restPreference: RestPreference.Frequent,
                budgetVnd: 200_000m);

            var totalPointsE2 = candidatesE2.Length + 2;
            var matrixDataE2 = new int[totalPointsE2, totalPointsE2];
            for (var i = 0; i < totalPointsE2; i++)
            {
                for (var j = 0; j < totalPointsE2; j++)
                {
                    matrixDataE2[i, j] = i == j ? 0 : 5;
                }
            }
            var matrixE2 = RouteDurationMatrix.Create(matrixDataE2);
            var (candsE2, idxE2) = Index(inputE2);
            list.Add(new GoldenScenario("tie-c30-m0-frequent-s32", "GroupE_Tie", true, inputE2, matrixE2, candsE2, idxE2,
                CspOptions: new CspOptions { MaxNodes = 130_000, TimeLimitMilliseconds = 120_000 }));

            // E3: Scenario tự nhiên 40 ứng viên, 1 bắt buộc, Frequent rest
            var (inputE3, matrixE3) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(40, 1, seed: 33, RestPreference.Frequent);
            var (candsE3, idxE3) = Index(inputE3);
            list.Add(new GoldenScenario("tie-c40-m1-frequent-s33", "GroupE_Tie", true, inputE3, matrixE3, candsE3, idxE3));
        }

        // =========================================================================
        // Nhóm F: Biên thời gian & Giây lẻ (2 kịch bản)
        // =========================================================================
        {
            // F1: StartAtUtc chứa 30 giây lẻ
            var (inputF1, matrixF1) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(15, 2, seed: 41, RestPreference.None);
            inputF1 = inputF1 with { StartAtUtc = inputF1.StartAtUtc.AddSeconds(30) };
            var (candsF1, idxF1) = Index(inputF1);
            list.Add(new GoldenScenario("boundary-fractional-seconds-s41", "GroupF_Boundary", true, inputF1, matrixF1, candsF1, idxF1));

            // F2: Điểm bắt buộc có giờ đóng cửa gấp (slack < 60 phút để kích hoạt MRV urgent)
            var (inputF2, matrixF2) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(15, 2, seed: 42, RestPreference.None);
            var firstMandatoryId = inputF2.MandatoryPoiIds.First();
            var tightCand = inputF2.Candidates.First(c => c.Id == firstMandatoryId) with
            {
                OpeningHours =
                [
                    new GenerationOpeningHours(2, new TimeOnly(8, 0), new TimeOnly(9, 30)),
                ],
            };
            inputF2 = inputF2 with
            {
                Candidates = inputF2.Candidates.Select(c => c.Id == tightCand.Id ? tightCand : c).ToArray(),
            };
            var (candsF2, idxF2) = Index(inputF2);
            list.Add(new GoldenScenario("boundary-urgent-slack-mrv-s42", "GroupF_Boundary", true, inputF2, matrixF2, candsF2, idxF2));
        }

        if (list.Count != 55)
        {
            throw new InvalidOperationException($"GoldenScenarioFactory must produce exactly 55 scenarios, but produced {list.Count}.");
        }

        return list;
    }

    private static (GenerationCandidate[] Candidates, Dictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.ToArray();
        return (candidates, candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index));
    }

    private static EvaluatedItinerarySchedule? HeuristicSchedule(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> candidates,
        IReadOnlyDictionary<long, int> indices,
        SchedulingGenerationOptions options)
    {
        var mandatory = input.MandatoryPoiIds.Select(id => candidates.Single(c => c.Id == id)).ToArray();
        return new OptionalRouteOptimizer(new ItineraryScheduleEvaluator(options), options)
            .Optimize(input, mandatory, matrix, candidates, indices)?.Schedule;
    }
}