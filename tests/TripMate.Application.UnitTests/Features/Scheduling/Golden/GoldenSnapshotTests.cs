using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden;

public sealed record DriftSummary(
    int TotalScenarios,
    int FeasibleScenarios,
    int DriftMeasuredScenarios,
    int MaxStartDriftOverall,
    int MaxTravelDiffOverall,
    IReadOnlyList<string> MeasuredScenarioIds,
    IReadOnlyList<DriftScenarioDetail> Details);

public sealed record DriftScenarioDetail(
    string ScenarioId,
    string Source,
    bool Feasible,
    bool DriftMeasured,
    int MaxStartDrift,
    int TravelDiff);

public class GoldenSnapshotTests
{
    private static readonly SchedulingGenerationOptions Options = new();
    private static readonly CspOptions GoldenCspOptions = new() { TimeLimitMilliseconds = 120_000 };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static IEnumerable<object[]> GetScenarios() =>
        GoldenScenarioFactory.CreateAllScenarios().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(GetScenarios))]
    public void Solver_MatchesGoldenSnapshot(GoldenScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, scenario.CspOptions ?? GoldenCspOptions);
        var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);

        var result = solver.Solve(
            scenario.Input,
            scenario.Matrix,
            scenario.Candidates,
            scenario.Indices,
            scenario.InitialIncumbent);

        var actualSnapshot = SnapshotMapper.From(scenario, result, primaryCtx);

        var snapshotDir = GetSnapshotsDirectory();
        var snapshotFile = Path.Combine(snapshotDir, $"{scenario.Id}.json");

        var isUpdateMode = Environment.GetEnvironmentVariable("TRIPMATE_GOLDEN_UPDATE") == "1";
        if (isUpdateMode)
        {
            var json = JsonSerializer.Serialize(actualSnapshot, JsonOptions);
            File.WriteAllText(snapshotFile, json);
            return;
        }

        if (!File.Exists(snapshotFile))
        {
            Assert.Fail($"Snapshot file not found: {snapshotFile}. Run with TRIPMATE_GOLDEN_UPDATE=1 to generate.");
        }

        var expectedJson = File.ReadAllText(snapshotFile);
        var expectedSnapshot = JsonSerializer.Deserialize<SolverGoldenSnapshot>(expectedJson, JsonOptions);
        actualSnapshot.Should().BeEquivalentTo(expectedSnapshot);
    }

    [Fact]
    public void GenerateDriftSummary_VerifiesZeroModelEvaluatorDrift()
    {
        var snapshotDir = GetSnapshotsDirectory();
        var scenarios = GoldenScenarioFactory.CreateAllScenarios();
        var details = new List<DriftScenarioDetail>(scenarios.Count);

        foreach (var scenario in scenarios)
        {
            var snapshotFile = Path.Combine(snapshotDir, $"{scenario.Id}.json");
            SolverGoldenSnapshot snapshot;
            if (File.Exists(snapshotFile))
            {
                var json = File.ReadAllText(snapshotFile);
                snapshot = JsonSerializer.Deserialize<SolverGoldenSnapshot>(json, JsonOptions)!;
            }
            else
            {
                var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, scenario.CspOptions ?? GoldenCspOptions);
                var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
                    scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);
                var result = solver.Solve(
                    scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, scenario.InitialIncumbent);
                snapshot = SnapshotMapper.From(scenario, result, primaryCtx);
            }

            details.Add(new DriftScenarioDetail(
                snapshot.ScenarioId,
                snapshot.Source,
                snapshot.Feasible,
                snapshot.DriftMeasured,
                snapshot.MaxModelVsEvaluatorDriftMinutes,
                snapshot.TravelMinutesModelVsEvaluatorDiff));
        }

        var maxStartDriftOverall = details.Count == 0 ? 0 : details.Max(d => d.MaxStartDrift);
        var maxTravelDiffOverall = details.Count == 0 ? 0 : details.Max(d => d.TravelDiff);
        var measuredScenarioIds = details.Where(d => d.DriftMeasured).Select(d => d.ScenarioId).ToList();

        var summary = new DriftSummary(
            TotalScenarios: details.Count,
            FeasibleScenarios: details.Count(d => d.Feasible),
            DriftMeasuredScenarios: details.Count(d => d.DriftMeasured),
            MaxStartDriftOverall: maxStartDriftOverall,
            MaxTravelDiffOverall: maxTravelDiffOverall,
            MeasuredScenarioIds: measuredScenarioIds,
            Details: details);

        var summaryFile = Path.Combine(snapshotDir, "_drift-summary.json");
        File.WriteAllText(summaryFile, JsonSerializer.Serialize(summary, JsonOptions));

        // 1. Phút di chuyển không bao giờ lệch giữa mô hình và evaluator (TravelDiff == 0)
        maxTravelDiffOverall.Should().Be(0, "Mô hình và Evaluator phải có cùng định nghĩa phút di chuyển trên mọi kịch bản không có điểm nghỉ");

        // 2. Trên mọi kịch bản thông thường (không có giây lẻ), drift mốc giờ bắt đầu phải bằng 0 tuyệt đối!
        var nonFractionalDetails = details.Where(d => d.ScenarioId != "boundary-fractional-seconds-s41" && d.DriftMeasured).ToList();
        if (nonFractionalDetails.Count > 0)
        {
            nonFractionalDetails.Max(d => d.MaxStartDrift).Should().Be(0, "Trên các kịch bản không có giây lẻ, Model Earliest[i] và Evaluator PlannedArrivalUtc phải trùng khớp 100%");
        }

        // 3. Kịch bản giây lẻ được đo lường và drift <= 1 phút do làm tròn trần
        measuredScenarioIds.Should().Contain("boundary-fractional-seconds-s41", "Kịch bản giây lẻ phải được đo lường drift (DriftMeasured == true)");
        var fractionalDetail = details.FirstOrDefault(d => d.ScenarioId == "boundary-fractional-seconds-s41");
        if (fractionalDetail is { DriftMeasured: true })
        {
            fractionalDetail.MaxStartDrift.Should().BeInRange(0, 1, "Kịch bản giây lẻ chỉ được phép lệch tối đa 1 phút do trần phút");
        }
    }

    [Fact]
    public void Golden_corpus_covers_every_solver_branch()
    {
        var snapshotDir = GetSnapshotsDirectory();
        var files = Directory.GetFiles(snapshotDir, "*.json")
            .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
            .ToArray();

        var all = files
            .Select(f => JsonSerializer.Deserialize<SolverGoldenSnapshot>(File.ReadAllText(f), JsonOptions)!)
            .ToList();

        all.Should().HaveCount(55, "Golden corpus phải chứa đúng 55 kịch bản");
        all.Should().Contain(s => s.PrunedByBound > 0, "Cần có kịch bản kích hoạt cắt tỉa nhánh Bound");
        all.Should().Contain(s => s.PrunedByForwardChecking > 0, "Cần có kịch bản kích hoạt cắt tỉa Forward Checking");
        all.Should().Contain(s => s.PrunedByConstraints > 0, "Cần có kịch bản vi phạm ràng buộc C1-C6");
        all.Should().Contain(s => s.InitialIncumbentAccepted, "Cần có kịch bản chấp nhận incumbent ban đầu");
        all.Should().Contain(s => s.ReturnedInitialIncumbent, "Cần có kịch bản incumbent ban đầu tối ưu và được trả về làm kết quả cuối cùng");
        all.Should().Contain(s => s.TieEvaluationLimitReached, "Cần có kịch bản chạm giới hạn đánh giá hòa Tie (4,096)");
        all.Should().Contain(s => s.InSearchEvaluationLimitReached, "Cần có kịch bản chạm giới hạn đánh giá trong lúc tìm kiếm (16,384)");
        all.Should().Contain(s => s.UsedRelaxedRestModel, "Cần có kịch bản phải chạy thêm mô hình nới lỏng (relaxed)");
        all.Should().Contain(s => s.ChoseRelaxedRestModel, "Cần có kịch bản mà kết quả tốt nhất chọn từ mô hình nới lỏng");
        all.Should().Contain(s => s.NodeLimitReached, "Cần có kịch bản chạm giới hạn số nút (đường thoát _aborted)");
        all.Should().Contain(s => !s.Feasible, "Cần có kịch bản chứng minh vô nghiệm (infeasible)");
        all.Should().Contain(s => s.EliteVisitIds.Count > 1, "Cần có kịch bản sinh ra nhiều hơn 1 lời giải ưu tú trong Elite");
    }

    private static string GetSnapshotsDirectory()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current) && !File.Exists(Path.Combine(current, "TripMate.slnx")))
        {
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        var snapshotDir = Path.Combine(current, "tests", "TripMate.Application.UnitTests", "Features", "Scheduling", "Golden", "Snapshots");
        Directory.CreateDirectory(snapshotDir);
        return snapshotDir;
    }
}