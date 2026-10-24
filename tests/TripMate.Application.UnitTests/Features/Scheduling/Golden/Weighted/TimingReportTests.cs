using System.Diagnostics;
using System.Text;
using System.Text.Json;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Application.UnitTests.Features.Scheduling.Golden.WeightedTimeFit;
using TripMate.Domain.Enums;

using Xunit;
using Xunit.Abstractions;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

public sealed record TimingBaselineEntry(
    string ScenarioId,
    string Category,
    int Candidates,
    int Mandatory,
    int NodesExpanded,
    double MedianMs,
    double NsPerNode,
    bool SearchCompleted);

public sealed record TimingBaselineReport(
    string GeneratedAtUtc,
    IReadOnlyList<TimingBaselineEntry> Entries);

public class TimingReportTests
{
    private readonly ITestOutputHelper _output;
    private static readonly SchedulingGenerationOptions Options = new();

    public TimingReportTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string GetWeightedDirectory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Features/Scheduling/Golden/Weighted"));

    private static (IReadOnlyList<GenerationCandidate> Candidates, IReadOnlyDictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.OrderBy(c => c.Id).ToList();
        var indices = candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        return (candidates, indices);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void MeasurePR07b_TimingBaselines()
    {
        (int n, int m, int seed, int availableMin, int? maxStops, int maxNodes)[] configs =
        [
            (12, 0, 81, 270, 5, 500_000),
            (12, 1, 82, 270, 5, 500_000),
            (13, 0, 83, 270, 5, 500_000),
            (13, 1, 84, 270, 5, 500_000),
            (14, 0, 85, 270, 5, 500_000),
            (14, 1, 86, 270, 5, 500_000),
            (15, 0, 87, 270, 5, 500_000),
            (15, 1, 88, 270, 5, 500_000)
        ];

        var entries = new List<TimingBaselineEntry>();
        const int iterations = 10;

        _output.WriteLine("=== BẢNG ĐO CHUẨN THỜI GIAN PR-07b (Trung vị 10 lần) ===");
        _output.WriteLine("| Nhóm | Kịch bản | n | m | Nodes | Thời gian (ms) | ns/node | Hoàn tất |");
        _output.WriteLine("| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: |");

        // 1. 8 Instance Constant TimeFit
        foreach (var (n, m, seed, availableMin, maxStops, maxNodes) in configs)
        {
            var id = $"const-n{n}-m{m}-s{seed}";
            var (rawInput, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
                n, m, seed, RestPreference.None);
            var input = rawInput with { AvailableMinutes = availableMin };
            var (cands, idx) = Index(input);
            var reqInputs = WeightedGoldenInputsFactory.CreateRequestInputs(cands);

            var cspOpts = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                MaxNodes = maxNodes,
                MaxStops = maxStops,
                TimeLimitMilliseconds = 60_000,
                PolishWithLocalSearch = false
            };
            var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOpts);

            // Warmup
            var r = solver.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);

            var timings = new double[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                solver.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);
                sw.Stop();
                timings[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(timings);
            var medianMs = timings[iterations / 2];
            var nsPerNode = r.Statistics.NodesExpanded > 0 ? (medianMs * 1_000_000.0) / r.Statistics.NodesExpanded : 0;

            _output.WriteLine($"| Constant | `{id}` | {n} | {m} | {r.Statistics.NodesExpanded:N0} | {medianMs:F2} ms | {nsPerNode:F1} | {r.Statistics.SearchCompleted} |");
            entries.Add(new TimingBaselineEntry(id, "Synthetic-Constant", n, m, r.Statistics.NodesExpanded, Math.Round(medianMs, 3), Math.Round(nsPerNode, 1), r.Statistics.SearchCompleted));
        }

        // 2. 8 Instance Real TimeFit
        foreach (var (n, m, seed, availableMin, maxStops, maxNodes) in configs)
        {
            var id = $"real-n{n}-m{m}-s{seed}";
            var (rawInput, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
                n, m, seed, RestPreference.None);
            var input = rawInput with { AvailableMinutes = availableMin };
            var (cands, idx) = Index(input);
            var reqInputs = WeightedTimeFitInputsFactory.CreateRequestInputs(cands);

            var cspOpts = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                MaxNodes = maxNodes,
                MaxStops = maxStops,
                TimeLimitMilliseconds = 60_000,
                PolishWithLocalSearch = false
            };
            var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOpts);

            // Warmup
            var r = solver.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);

            var timings = new double[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                solver.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);
                sw.Stop();
                timings[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(timings);
            var medianMs = timings[iterations / 2];
            var nsPerNode = r.Statistics.NodesExpanded > 0 ? (medianMs * 1_000_000.0) / r.Statistics.NodesExpanded : 0;

            _output.WriteLine($"| RealTimeFit | `{id}` | {n} | {m} | {r.Statistics.NodesExpanded:N0} | {medianMs:F2} ms | {nsPerNode:F1} | {r.Statistics.SearchCompleted} |");
            entries.Add(new TimingBaselineEntry(id, "Synthetic-RealTimeFit", n, m, r.Statistics.NodesExpanded, Math.Round(medianMs, 3), Math.Round(nsPerNode, 1), r.Statistics.SearchCompleted));
        }

        // 3. 5 kịch bản Golden c40 lớn nhất ở Weighted-TimeFit
        string[] c40Ids =
        [
            "grid-c40-m0-none-s1",
            "grid-c40-m1-none-s1",
            "grid-c40-m3-none-s1",
            "grid-c40-m6-none-s1",
            "incumbent-valid-c40-m3-frequent-s12"
        ];

        var allScenarios = WeightedScenarioFactory.CreateAllScenarios();
        foreach (var c40Id in c40Ids)
        {
            var scenario = allScenarios.First(s => s.Id == c40Id);
            var reqInputs = WeightedTimeFitInputsFactory.CreateRequestInputs(scenario.Candidates);
            var baseCspOpts = scenario.CspOptions ?? new CspOptions { TimeLimitMilliseconds = 120_000 };
            var cspOpts = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                MaxNodes = baseCspOpts.MaxNodes,
                MaxStops = baseCspOpts.MaxStops,
                MaxOptionalDomainSize = baseCspOpts.MaxOptionalDomainSize,
                FinalCandidatesToValidate = baseCspOpts.FinalCandidatesToValidate,
                TimeLimitMilliseconds = baseCspOpts.TimeLimitMilliseconds,
                PolishWithLocalSearch = false
            };
            var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOpts);

            // Warmup
            var r = solver.Solve(scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, scenario.InitialIncumbent, reqInputs, CancellationToken.None);

            var timings = new double[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                solver.Solve(scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, scenario.InitialIncumbent, reqInputs, CancellationToken.None);
                sw.Stop();
                timings[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(timings);
            var medianMs = timings[iterations / 2];
            var nsPerNode = r.Statistics.NodesExpanded > 0 ? (medianMs * 1_000_000.0) / r.Statistics.NodesExpanded : 0;

            _output.WriteLine($"| Golden c40 | `{c40Id}` | {scenario.Candidates.Count} | {scenario.Input.MandatoryPoiIds.Count} | {r.Statistics.NodesExpanded:N0} | {medianMs:F2} ms | {nsPerNode:F1} | {r.Statistics.SearchCompleted} |");
            entries.Add(new TimingBaselineEntry(c40Id, "Golden-c40", scenario.Candidates.Count, scenario.Input.MandatoryPoiIds.Count, r.Statistics.NodesExpanded, Math.Round(medianMs, 3), Math.Round(nsPerNode, 1), r.Statistics.SearchCompleted));
        }

        var report = new TimingBaselineReport(DateTimeOffset.UtcNow.ToString("O"), entries);
        var jsonText = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var reportPath = Path.Combine(GetWeightedDirectory(), "_timing-baseline-pr07b.json");
        File.WriteAllText(reportPath, jsonText.Replace("\r\n", "\n") + "\n", Encoding.UTF8);

        Assert.Equal(21, entries.Count);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void MeasurePR07c_Timing()
    {
        var baselinePath = Path.Combine(GetWeightedDirectory(), "_timing-baseline-pr07b.json");
        TimingBaselineReport? baselineReport = null;
        if (File.Exists(baselinePath))
        {
            var text = File.ReadAllText(baselinePath);
            baselineReport = JsonSerializer.Deserialize<TimingBaselineReport>(text);
        }
        var baselineMap = baselineReport?.Entries.ToDictionary(e => e.ScenarioId) ?? new();

        (int n, int m, int seed, int availableMin, int? maxStops, int maxNodes)[] configs =
        [
            (12, 0, 81, 270, 5, 500_000),
            (12, 1, 82, 270, 5, 500_000),
            (13, 0, 83, 270, 5, 500_000),
            (13, 1, 84, 270, 5, 500_000),
            (14, 0, 85, 270, 5, 500_000),
            (14, 1, 86, 270, 5, 500_000),
            (15, 0, 87, 270, 5, 500_000),
            (15, 1, 88, 270, 5, 500_000)
        ];

        var entries = new List<TimingBaselineEntry>();
        const int iterations = 10;

        _output.WriteLine("=== SO SÁNH THỜI GIAN VÀ CHI PHÍ NODE: PR-07c vs PR-07b (Trung vị 10 lần) ===");
        _output.WriteLine("| Nhóm | Kịch bản | n | m | Nodes PR-07b | Nodes PR-07c | ms PR-07b | ms PR-07c | ns/node PR-07b | ns/node PR-07c | Tăng tốc |");
        _output.WriteLine("| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

        // 1. 8 Instance Constant TimeFit
        foreach (var (n, m, seed, availableMin, maxStops, maxNodes) in configs)
        {
            var id = $"const-n{n}-m{m}-s{seed}";
            var (rawInput, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
                n, m, seed, RestPreference.None);
            var input = rawInput with { AvailableMinutes = availableMin };
            var (cands, idx) = Index(input);
            var reqInputs = WeightedGoldenInputsFactory.CreateRequestInputs(cands);

            var cspOpts = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                MaxNodes = maxNodes,
                MaxStops = maxStops,
                TimeLimitMilliseconds = 60_000,
                PolishWithLocalSearch = false
            };
            var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOpts);

            var r = solver.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);

            var timings = new double[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                solver.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);
                sw.Stop();
                timings[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(timings);
            var medianMs = timings[iterations / 2];
            var nsPerNode = r.Statistics.NodesExpanded > 0 ? (medianMs * 1_000_000.0) / r.Statistics.NodesExpanded : 0;

            baselineMap.TryGetValue(id, out var b);
            var speedup = b != null && medianMs > 0 ? b.MedianMs / medianMs : 1.0;
            _output.WriteLine($"| Constant | `{id}` | {n} | {m} | {b?.NodesExpanded.ToString("N0") ?? "-"} | {r.Statistics.NodesExpanded:N0} | {b?.MedianMs:F2} | {medianMs:F2} | {b?.NsPerNode:F1} | {nsPerNode:F1} | {speedup:F2}x |");
            entries.Add(new TimingBaselineEntry(id, "Synthetic-Constant", n, m, r.Statistics.NodesExpanded, Math.Round(medianMs, 3), Math.Round(nsPerNode, 1), r.Statistics.SearchCompleted));
        }

        // 2. 8 Instance Real TimeFit
        foreach (var (n, m, seed, availableMin, maxStops, maxNodes) in configs)
        {
            var id = $"real-n{n}-m{m}-s{seed}";
            var (rawInput, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
                n, m, seed, RestPreference.None);
            var input = rawInput with { AvailableMinutes = availableMin };
            var (cands, idx) = Index(input);
            var reqInputs = WeightedTimeFitInputsFactory.CreateRequestInputs(cands);

            var cspOpts = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                MaxNodes = maxNodes,
                MaxStops = maxStops,
                TimeLimitMilliseconds = 60_000,
                PolishWithLocalSearch = false
            };
            var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOpts);

            var r = solver.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);

            var timings = new double[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                solver.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);
                sw.Stop();
                timings[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(timings);
            var medianMs = timings[iterations / 2];
            var nsPerNode = r.Statistics.NodesExpanded > 0 ? (medianMs * 1_000_000.0) / r.Statistics.NodesExpanded : 0;

            baselineMap.TryGetValue(id, out var b);
            var speedup = b != null && medianMs > 0 ? b.MedianMs / medianMs : 1.0;
            _output.WriteLine($"| RealTimeFit | `{id}` | {n} | {m} | {b?.NodesExpanded.ToString("N0") ?? "-"} | {r.Statistics.NodesExpanded:N0} | {b?.MedianMs:F2} | {medianMs:F2} | {b?.NsPerNode:F1} | {nsPerNode:F1} | {speedup:F2}x |");
            entries.Add(new TimingBaselineEntry(id, "Synthetic-RealTimeFit", n, m, r.Statistics.NodesExpanded, Math.Round(medianMs, 3), Math.Round(nsPerNode, 1), r.Statistics.SearchCompleted));
        }

        // 3. 5 kịch bản Golden c40 lớn nhất ở Weighted-TimeFit
        string[] c40Ids =
        [
            "grid-c40-m0-none-s1",
            "grid-c40-m1-none-s1",
            "grid-c40-m3-none-s1",
            "grid-c40-m6-none-s1",
            "incumbent-valid-c40-m3-frequent-s12"
        ];

        var allScenarios = WeightedScenarioFactory.CreateAllScenarios();
        foreach (var c40Id in c40Ids)
        {
            var scenario = allScenarios.First(s => s.Id == c40Id);
            var reqInputs = WeightedTimeFitInputsFactory.CreateRequestInputs(scenario.Candidates);
            var baseCspOpts = scenario.CspOptions ?? new CspOptions { TimeLimitMilliseconds = 120_000 };
            var cspOpts = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                MaxNodes = baseCspOpts.MaxNodes,
                MaxStops = baseCspOpts.MaxStops,
                MaxOptionalDomainSize = baseCspOpts.MaxOptionalDomainSize,
                FinalCandidatesToValidate = baseCspOpts.FinalCandidatesToValidate,
                TimeLimitMilliseconds = baseCspOpts.TimeLimitMilliseconds,
                PolishWithLocalSearch = false
            };
            var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOpts);

            var r = solver.Solve(scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, scenario.InitialIncumbent, reqInputs, CancellationToken.None);

            var timings = new double[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                solver.Solve(scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, scenario.InitialIncumbent, reqInputs, CancellationToken.None);
                sw.Stop();
                timings[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(timings);
            var medianMs = timings[iterations / 2];
            var nsPerNode = r.Statistics.NodesExpanded > 0 ? (medianMs * 1_000_000.0) / r.Statistics.NodesExpanded : 0;

            baselineMap.TryGetValue(c40Id, out var b);
            var speedup = b != null && medianMs > 0 ? b.MedianMs / medianMs : 1.0;
            _output.WriteLine($"| Golden c40 | `{c40Id}` | {scenario.Candidates.Count} | {scenario.Input.MandatoryPoiIds.Count} | {b?.NodesExpanded.ToString("N0") ?? "-"} | {r.Statistics.NodesExpanded:N0} | {b?.MedianMs:F2} | {medianMs:F2} | {b?.NsPerNode:F1} | {nsPerNode:F1} | {speedup:F2}x |");
            entries.Add(new TimingBaselineEntry(c40Id, "Golden-c40", scenario.Candidates.Count, scenario.Input.MandatoryPoiIds.Count, r.Statistics.NodesExpanded, Math.Round(medianMs, 3), Math.Round(nsPerNode, 1), r.Statistics.SearchCompleted));
        }

        var report = new TimingBaselineReport(DateTimeOffset.UtcNow.ToString("O"), entries);
        var jsonText = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var reportPath = Path.Combine(GetWeightedDirectory(), "_timing-report-pr07c.json");
        File.WriteAllText(reportPath, jsonText.Replace("\r\n", "\n") + "\n", Encoding.UTF8);

        Assert.Equal(21, entries.Count);
    }
}
