using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Fixtures;

/// <summary>Môi trường, tham số và corpus của một lần chạy <c>tools/benchmarks/solver-comparison</c>.</summary>
/// <param name="SourceHash">
/// SHA-256 (hex thường) của các tệp nguồn quyết định kết quả benchmark (<see cref="SolverComparisonReport.SourceInputs"/>),
/// xem <see cref="SolverComparisonReport.ComputeSourceHash"/>. Không phụ thuộc git, nên không đổi khi squash hay rebase.
/// </param>
public sealed record SolverComparisonMetadata(
    string Date,
    string SourceHash,
    int SourceFileCount,
    string GeneratedAtUtc,
    string OperatingSystem,
    string Processor,
    int LogicalProcessors,
    string Runtime,
    string BuildConfiguration,
    int WarmupIterations,
    int MeasuredIterations,
    int Seeds,
    IReadOnlyList<int> CandidateCounts,
    IReadOnlyList<int> MandatoryCounts,
    IReadOnlyList<string> RestPreferences,
    int NamedScenarioCount,
    int MaxMatrixCandidates,
    CspOptions Csp);

/// <summary>
/// Một scenario: kết quả so sánh theo product rule và outcome thực của từng chế độ, lấy từ lần chạy warmup
/// (lần chạy sinh ra kế hoạch được so sánh). Giá trị null nghĩa là chế độ đó không tạo được kế hoạch.
/// </summary>
public sealed record SolverComparisonScenarioRow(
    string Segment,
    string Scenario,
    string RestPreference,
    int MandatoryCount,
    string Verdict,
    int? HeuristicVisits,
    int? CspVisits,
    int? HeuristicOptional,
    int? CspOptional,
    int? HeuristicTravel,
    int? CspTravel,
    int? HeuristicDuration,
    int? CspDuration,
    string DisabledOutcome,
    string HeuristicOutcome,
    string CspOutcome,
    int? CspNodes,
    int? CspSolutionsFound,
    bool? CspSearchCompleted,
    bool? CspNodeLimitReached,
    bool? CspTimeLimitReached);

/// <summary>Một lần chạy đo thời gian (không gồm warmup) và outcome thực của chính lần chạy đó.</summary>
public sealed record SolverComparisonTimingRow(
    string Segment,
    string Scenario,
    string Mode,
    int Iteration,
    double ElapsedMs,
    string Outcome);

public sealed record SolverComparisonData(
    SolverComparisonMetadata Metadata,
    IReadOnlyList<SolverComparisonScenarioRow> Scenarios,
    IReadOnlyList<SolverComparisonTimingRow> Timings);

/// <summary>
/// Định dạng dữ liệu và báo cáo của benchmark so sánh bộ giải. Báo cáo Markdown là một hàm thuần của ba tệp
/// dữ liệu (<c>.csv</c>, <c>.timings.csv</c>, <c>.meta.json</c>), nên mọi con số trong báo cáo đều dựng lại được
/// từ tệp đã commit; <c>SolverComparisonReportTests</c> kiểm tra điều đó.
/// </summary>
public static class SolverComparisonReport
{
    public const string DisabledMode = "disabled";
    public const string HeuristicMode = "heuristic";
    public const string CspMode = "csp";
    public const string FallbackOutcomePrefix = "heuristic_fallback";
    public const double AddedP95GateMilliseconds = 300;

    /// <summary>
    /// Tệp và thư mục (đường dẫn tương đối từ gốc repository) mà kết quả benchmark phụ thuộc: bộ giải và evaluator,
    /// bộ sinh corpus, quy tắc so sánh và runner. Thư mục được lấy mọi tệp <c>*.cs</c> bên trong.
    /// </summary>
    public static readonly IReadOnlyList<string> SourceInputs =
    [
        "src/TripMate.Application/Features/Scheduling/Common",
        "src/TripMate.Application/Features/Scheduling/Csp",
        "src/TripMate.Application/Features/Scheduling/Routing",
        "tests/TripMate.Application.UnitTests/Features/Scheduling/Fixtures/OptionalRouteOptimizationScenarios.cs",
        "tests/TripMate.Application.UnitTests/Features/Scheduling/Fixtures/ProductRulePlanComparison.cs",
        "tools/benchmarks/solver-comparison/Program.cs",
    ];

    private const string ScenarioHeader =
        "segment,scenario,rest_preference,mandatory_count,csp_vs_heuristic,heuristic_visits,csp_visits,heuristic_optional,csp_optional,"
        + "heuristic_travel,csp_travel,heuristic_duration,csp_duration,disabled_outcome,heuristic_outcome,csp_outcome,"
        + "csp_nodes,csp_solutions_found,csp_search_completed,csp_node_limit,csp_time_limit";

    private const string TimingHeader = "segment,scenario,mode,iteration,elapsed_ms,outcome";

    private const int MaxReportedProblems = 20;

    private static readonly string[] Modes = [DisabledMode, HeuristicMode, CspMode];

    private static readonly string[] Verdicts = ["better", "equal", "worse", "excluded"];

    // Outcome mà từng chế độ có thể ghi: chế độ disabled và heuristic không bao giờ chạy CSP.
    private static readonly string[] HeuristicModeOutcomes =
        [SchedulingSolverOutcomes.Heuristic, SchedulingSolverOutcomes.Infeasible];

    private static readonly string[] CspModeOutcomes =
    [
        SchedulingSolverOutcomes.Csp,
        SchedulingSolverOutcomes.HeuristicFallbackCspProvedInfeasible,
        SchedulingSolverOutcomes.HeuristicFallbackCspNoValidCandidate,
        SchedulingSolverOutcomes.HeuristicFallbackCspSearchLimit,
        SchedulingSolverOutcomes.Infeasible,
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string BaseName(string date) => $"UC-10-csp-solver-comparison-{date}";

    public static void Write(string directory, SolverComparisonData data)
    {
        Validate(data);
        Directory.CreateDirectory(directory);
        var baseName = BaseName(data.Metadata.Date);
        File.WriteAllText(Path.Combine(directory, baseName + ".meta.json"), JsonSerializer.Serialize(data.Metadata, JsonOptions) + "\n");
        File.WriteAllText(Path.Combine(directory, baseName + ".csv"), ScenarioCsv(data.Scenarios));
        File.WriteAllText(Path.Combine(directory, baseName + ".timings.csv"), TimingCsv(data.Timings));

        // Dựng báo cáo từ chính các tệp vừa ghi, để báo cáo luôn khớp từng chữ số với dữ liệu đã lưu.
        var persisted = Read(directory, baseName);
        File.WriteAllText(Path.Combine(directory, baseName + ".md"), Markdown(persisted));
    }

    public static SolverComparisonData Read(string directory, string baseName)
    {
        var metadata = JsonSerializer.Deserialize<SolverComparisonMetadata>(
            File.ReadAllText(Path.Combine(directory, baseName + ".meta.json")), JsonOptions)
            ?? throw new InvalidDataException($"{baseName}.meta.json is empty.");
        if (BaseName(metadata.Date) != baseName)
        {
            throw new InvalidDataException($"{baseName}.meta.json is dated {metadata.Date}, which does not match its file name.");
        }

        var scenarios = ReadCsv(Path.Combine(directory, baseName + ".csv"), ScenarioHeader, ParseScenario);
        var timings = ReadCsv(Path.Combine(directory, baseName + ".timings.csv"), TimingHeader, ParseTiming);
        var data = new SolverComparisonData(metadata, scenarios, timings);
        Validate(data);
        return data;
    }

    /// <summary>
    /// Từ chối dữ liệu thiếu hoặc sai, để không mẫu nào bị loại âm thầm khỏi báo cáo. Dữ liệu hợp lệ khi:
    /// <list type="bullet">
    /// <item>hash nguồn là SHA-256 đủ 64 ký tự hex thường; có ít nhất một lần warmup và một lần đo; mọi kiểu nghỉ
    /// khai báo là giá trị hợp lệ của <see cref="RestPreference"/>;</item>
    /// <item>số scenario đúng bằng corpus khai báo trong metadata, tên không trùng, verdict, kiểu nghỉ và outcome thuộc
    /// tập cho phép, và kế hoạch do CSP tạo không vượt <c>MaxStops</c>;</item>
    /// <item>mỗi dòng timing có mode và outcome cho phép, thời gian hữu hạn và không âm, scenario có thật với đúng
    /// segment của nó, iteration trong [1, MeasuredIterations], và mỗi bộ (scenario, mode, iteration) xuất hiện
    /// đúng một lần — tức số dòng bằng đúng scenario × mode × MeasuredIterations.</item>
    /// </list>
    /// Ném <see cref="InvalidDataException"/> liệt kê các lỗi tìm thấy.
    /// </summary>
    public static void Validate(SolverComparisonData data)
    {
        var problems = new List<string>();
        var meta = data.Metadata;
        ValidateMetadata(meta, problems);
        if (meta.CandidateCounts is null || meta.MandatoryCounts is null || meta.RestPreferences is null || meta.Csp is null)
        {
            Throw(problems);
        }

        var expectedScenarios = meta.NamedScenarioCount
            + (meta.CandidateCounts!.Count * meta.MandatoryCounts!.Count * meta.RestPreferences!.Count * meta.Seeds);
        if (data.Scenarios.Count != expectedScenarios)
        {
            problems.Add(Invariant($"expected {expectedScenarios} scenarios for the declared corpus, found {data.Scenarios.Count}"));
        }

        var segmentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in data.Scenarios)
        {
            if (!segmentOf.TryAdd(row.Scenario, row.Segment))
            {
                problems.Add($"scenario {row.Scenario} appears more than once");
            }

            ValidateScenario(row, meta.Csp!, problems);
        }

        var keys = new HashSet<(string Scenario, string Mode, int Iteration)>();
        foreach (var timing in data.Timings)
        {
            ValidateTiming(timing, meta.MeasuredIterations, segmentOf, problems);
            if (!keys.Add((timing.Scenario, timing.Mode, timing.Iteration)))
            {
                problems.Add(Invariant($"timing {timing.Scenario} / {timing.Mode} / iteration {timing.Iteration} appears more than once"));
            }
        }

        var expectedTimings = segmentOf.Count * Modes.Length * Math.Max(meta.MeasuredIterations, 0);
        if (data.Timings.Count != expectedTimings)
        {
            problems.Add(Invariant($"expected {expectedTimings} timing rows (scenarios × modes × measured iterations), found {data.Timings.Count}"));
        }

        foreach (var scenario in segmentOf.Keys)
        {
            foreach (var mode in Modes)
            {
                for (var iteration = 1; iteration <= meta.MeasuredIterations; iteration++)
                {
                    if (!keys.Contains((scenario, mode, iteration)))
                    {
                        problems.Add(Invariant($"timing {scenario} / {mode} / iteration {iteration} is missing"));
                    }
                }
            }
        }

        Throw(problems);
    }

    /// <summary>
    /// Hash nội dung của <paramref name="inputs"/> (mặc định <see cref="SourceInputs"/>) dưới
    /// <paramref name="repositoryRoot"/>: SHA-256 trên từng tệp theo thứ tự đường dẫn (dạng <c>/</c>, so sánh ordinal),
    /// mỗi tệp góp "đường dẫn \n nội dung \0" với xuống dòng chuẩn hóa về LF. Nhờ vậy hash giống nhau trên Windows và
    /// Linux, trong source archive không có <c>.git</c>, và sau khi squash hay rebase.
    /// </summary>
    public static (string Hash, int FileCount) ComputeSourceHash(string repositoryRoot, IReadOnlyList<string>? inputs = null)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in inputs ?? SourceInputs)
        {
            var path = Path.Combine(repositoryRoot, input);
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                {
                    files[Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/')] = file;
                }
            }
            else if (File.Exists(path))
            {
                files[input.Replace('\\', '/')] = path;
            }
            else
            {
                throw new FileNotFoundException($"Benchmark source input '{input}' does not exist under {repositoryRoot}.", path);
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relativePath, file) in files)
        {
            var content = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath + "\n" + content + "\0"));
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), files.Count);
    }

    /// <summary>
    /// Báo cáo phải đo đúng mã nguồn hiện tại: hash ghi trong metadata bằng hash tính lại trên repository. Ném
    /// <see cref="InvalidDataException"/> kèm lệnh chạy lại benchmark nếu không.
    /// </summary>
    public static void EnsureMatchesSource(SolverComparisonMetadata metadata, string currentHash)
    {
        if (!string.Equals(metadata.SourceHash, currentHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"the report was measured on source {metadata.SourceHash}, but the benchmarked files now hash to {currentHash}. "
                + "Regenerate it: dotnet run -c Release --project tools/benchmarks/solver-comparison");
        }
    }

    public static string ScenarioCsv(IEnumerable<SolverComparisonScenarioRow> rows)
    {
        var csv = new StringBuilder(ScenarioHeader).Append('\n');
        foreach (var r in rows)
        {
            csv.AppendJoin(',',
                r.Segment, r.Scenario, r.RestPreference, Format(r.MandatoryCount), r.Verdict,
                Format(r.HeuristicVisits), Format(r.CspVisits), Format(r.HeuristicOptional), Format(r.CspOptional),
                Format(r.HeuristicTravel), Format(r.CspTravel), Format(r.HeuristicDuration), Format(r.CspDuration),
                r.DisabledOutcome, r.HeuristicOutcome, r.CspOutcome,
                Format(r.CspNodes), Format(r.CspSolutionsFound), Format(r.CspSearchCompleted),
                Format(r.CspNodeLimitReached), Format(r.CspTimeLimitReached));
            csv.Append('\n');
        }

        return csv.ToString();
    }

    public static string TimingCsv(IEnumerable<SolverComparisonTimingRow> rows)
    {
        var csv = new StringBuilder(TimingHeader).Append('\n');
        foreach (var r in rows)
        {
            csv.AppendJoin(',', r.Segment, r.Scenario, r.Mode, Format(r.Iteration),
                r.ElapsedMs.ToString("F4", CultureInfo.InvariantCulture), r.Outcome);
            csv.Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>
    /// Phân vị theo hạng gần nhất: phần tử ở chỉ số floor(n × p) của dãy mẫu đã sắp tăng dần
    /// (chặn ở n − 1). Đây là định nghĩa được công bố trong báo cáo.
    /// </summary>
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * percentile))];
    }

    public static bool IsFallback(string outcome) =>
        outcome.StartsWith(FallbackOutcomePrefix, StringComparison.Ordinal);

    public static string Markdown(SolverComparisonData data)
    {
        Validate(data);
        var meta = data.Metadata;
        var csp = meta.Csp;
        var rows = data.Scenarios;
        var baseName = BaseName(meta.Date);
        var segments = rows.Select(r => r.Segment).Distinct().ToArray();
        var md = new StringBuilder();

        md.AppendLine(Invariant($"# UC-10 CSP Solver Comparison — {meta.Date}"));
        md.AppendLine();
        md.AppendLine("> Generated by `tools/benchmarks/solver-comparison`; do not edit by hand. Every number below is");
        md.AppendLine("> recomputed from the three data files, and `SolverComparisonReportTests` fails if this file differs");
        md.AppendLine("> from that recomputation.");
        md.AppendLine();
        md.AppendLine("## Environment and corpus");
        md.AppendLine();
        md.AppendLine(Invariant($"- Source: SHA-256 `{meta.SourceHash}` over {meta.SourceFileCount} benchmarked files (`SolverComparisonReport.SourceInputs`: the scheduling `Common`, `Csp` and `Routing` folders, the corpus fixture, the comparison rule and the runner; line endings normalized to LF). `SolverComparisonReportTests` recomputes it, so this report always matches the committed source, whatever the merge strategy."));
        md.AppendLine(Invariant($"- Generated at (UTC): {meta.GeneratedAtUtc}"));
        md.AppendLine(Invariant($"- Host: {meta.OperatingSystem}; CPU: {meta.Processor}; logical processors: {meta.LogicalProcessors}; runtime: {meta.Runtime}; build: {meta.BuildConfiguration}"));
        md.AppendLine(Invariant($"- Iterations: {meta.WarmupIterations} warmup + {meta.MeasuredIterations} measured per scenario and mode, sequential, in one process. The warmup run produces the compared plan, its outcome and its CSP statistics; only measured runs are timed."));
        md.AppendLine("- Modes: `disabled` = Heuristic with `EnableOptionalRouteOptimization = false` (latency baseline); `heuristic` = `SolverMode = Heuristic` (production default); `csp` = `SolverMode = Csp`.");
        md.AppendLine(Invariant($"- CSP options: MaxStops {csp.MaxStops}, MaxOptionalDomainSize {csp.MaxOptionalDomainSize}, MaxNodes {csp.MaxNodes}, TimeLimitMilliseconds {csp.TimeLimitMilliseconds}, FinalCandidatesToValidate {csp.FinalCandidatesToValidate}, UseInitialIncumbent {csp.UseInitialIncumbent}, PolishWithLocalSearch {csp.PolishWithLocalSearch}. Other scheduling options at their defaults (MaxMatrixCandidates {meta.MaxMatrixCandidates})."));
        md.AppendLine(Invariant($"- Corpus: **synthetic only**. {meta.NamedScenarioCount} named `OptionalRouteOptimizationScenarios` fixtures (rest preference `None`) plus `CreateSyntheticCorpusScenario` (600 available minutes, 30-minute visits) for candidates {{{string.Join(", ", meta.CandidateCounts)}}} × mandatory {{{string.Join(", ", meta.MandatoryCounts)}}} × rest preference {{{string.Join(", ", meta.RestPreferences)}}} × seeds 1–{meta.Seeds}. The generator is seeded and the rest preference does not change the instance, so each seed is the same trip under every rest preference. Segments without a suffix use `None`; `-auto` and `-frequent` segments use `Auto` (the mobile default) and `Frequent`. Sample size {rows.Count}."));
        md.AppendLine(Invariant($"- Excluded (a solver produced no plan): {rows.Count(r => r.Verdict == "excluded")}"));
        md.AppendLine("- Comparison rule: the product rule of `ScheduleGlobalComparator` (optional inclusion in canonical rank order, then matrix travel, duration, end time, visit IDs).");
        md.AppendLine("- Outcomes are recorded by the service itself (`SchedulingSolverDiagnostics`, activity `SchedulingSolverOutcome`) on the same run, not inferred from solver statistics.");
        md.AppendLine(Invariant($"- Data files: `{baseName}.csv` (one row per scenario), `{baseName}.timings.csv` (every measured run), `{baseName}.meta.json` (this section)."));
        md.AppendLine();

        md.AppendLine("## Quality: CSP vs Heuristic under the product rule");
        md.AppendLine();
        md.AppendLine("| Segment | n | CSP better | Equal | CSP worse | Avg optional (H / CSP) | Avg travel min (H / CSP) | Avg duration min (H / CSP) |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var segment in segments)
        {
            var group = rows.Where(r => r.Segment == segment).ToArray();
            var included = group.Where(r => r.Verdict != "excluded").ToArray();
            md.AppendLine(
                Invariant($"| {segment} | {group.Length} | {included.Count(r => r.Verdict == "better")} | {included.Count(r => r.Verdict == "equal")} | {included.Count(r => r.Verdict == "worse")} ")
                + Invariant($"| {Average(included, r => r.HeuristicOptional):F2} / {Average(included, r => r.CspOptional):F2} ")
                + Invariant($"| {Average(included, r => r.HeuristicTravel):F1} / {Average(included, r => r.CspTravel):F1} ")
                + Invariant($"| {Average(included, r => r.HeuristicDuration):F1} / {Average(included, r => r.CspDuration):F1} |"));
        }

        md.AppendLine();
        md.AppendLine("### By rest preference");
        md.AppendLine();
        md.AppendLine("| Rest preference | n | CSP better | Equal | CSP worse | Excluded |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var preference in rows.Select(r => r.RestPreference).Distinct())
        {
            var group = rows.Where(r => r.RestPreference == preference).ToArray();
            md.AppendLine(Invariant(
                $"| {preference} | {group.Length} | {group.Count(r => r.Verdict == "better")} | {group.Count(r => r.Verdict == "equal")} | {group.Count(r => r.Verdict == "worse")} | {group.Count(r => r.Verdict == "excluded")} |"));
        }

        md.AppendLine();
        md.AppendLine("## CSP limits");
        md.AppendLine();
        var cspVisits = rows.Where(r => r.CspOutcome == SchedulingSolverOutcomes.Csp && r.CspVisits is not null).ToArray();
        md.AppendLine(Invariant($"- Plans produced by the CSP: {cspVisits.Length}; most visits in one plan: {(cspVisits.Length == 0 ? 0 : cspVisits.Max(r => r.CspVisits!.Value))}; plans with more than MaxStops ({csp.MaxStops}) visits: {cspVisits.Count(r => r.CspVisits > csp.MaxStops)}."));
        md.AppendLine("- Plans the heuristic produced after a CSP fallback are not subject to the CSP limits and are excluded from this check.");
        md.AppendLine();

        md.AppendLine("## Solver outcome (warmup run of each scenario)");
        md.AppendLine();
        md.AppendLine("| Outcome | Disabled | Heuristic | CSP |");
        md.AppendLine("|---|---:|---:|---:|");
        var outcomes = rows.SelectMany(r => new[] { r.DisabledOutcome, r.HeuristicOutcome, r.CspOutcome })
            .Distinct()
            .OrderBy(outcome => outcome, StringComparer.Ordinal);
        foreach (var outcome in outcomes)
        {
            md.AppendLine(Invariant(
                $"| `{outcome}` | {rows.Count(r => r.DisabledOutcome == outcome)} | {rows.Count(r => r.HeuristicOutcome == outcome)} | {rows.Count(r => r.CspOutcome == outcome)} |"));
        }

        var mismatched = data.Timings.Count(t => t.Outcome != WarmupOutcome(rows, t));
        md.AppendLine();
        md.AppendLine(Invariant($"Measured runs whose outcome differs from the warmup run of the same scenario and mode: {mismatched}."));
        md.AppendLine();

        md.AppendLine("## Latency (generator only, milliseconds)");
        md.AppendLine();
        md.AppendLine(Invariant($"pXX is the sample at index floor(n × XX / 100) of the segment's measured runs sorted ascending (n = scenarios × {meta.MeasuredIterations}). Added p95 = CSP p95 − disabled p95."));
        md.AppendLine();
        md.AppendLine("| Segment | Disabled p50 / p95 / max | Heuristic p50 / p95 / max | CSP p50 / p95 / max | CSP added p95 (gate ≤ 300) | Node limit hit | Time limit hit | CSP fell back to heuristic |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        var failedLatency = new List<string>();
        foreach (var segment in segments)
        {
            var group = rows.Where(r => r.Segment == segment).ToArray();
            var disabled = Samples(data.Timings, segment, DisabledMode);
            var heuristic = Samples(data.Timings, segment, HeuristicMode);
            var cspSamples = Samples(data.Timings, segment, CspMode);
            var added = Percentile(cspSamples, 0.95) - Percentile(disabled, 0.95);
            if (added > AddedP95GateMilliseconds)
            {
                failedLatency.Add(segment);
            }

            md.AppendLine(
                Invariant($"| {segment} | {Summary(disabled)} | {Summary(heuristic)} | {Summary(cspSamples)} ")
                + Invariant($"| {added:F2} {(added <= AddedP95GateMilliseconds ? "✅" : "❌")} ")
                + Invariant($"| {group.Count(r => r.CspNodeLimitReached == true)} | {group.Count(r => r.CspTimeLimitReached == true)} | {group.Count(r => IsFallback(r.CspOutcome))} |"));
        }

        var worse = rows.Where(r => r.Verdict == "worse").Select(r => r.Scenario).ToArray();
        var fellBack = rows.Where(r => IsFallback(r.CspOutcome)).Select(r => r.Scenario).ToArray();
        md.AppendLine();
        md.AppendLine("## Gate status (Phase 3 default-switch gate; CSP stays experimental until all pass)");
        md.AppendLine();
        md.AppendLine(Invariant($"- Quality (100% better or equal): {(worse.Length == 0 ? "passed" : $"**failed** in {worse.Length} of {rows.Count} scenarios")}"));
        if (worse.Length > 0)
        {
            md.AppendLine(Invariant($"  - CSP worse in: {string.Join(", ", worse)}"));
        }

        md.AppendLine(Invariant($"- Latency (added p95 ≤ {AddedP95GateMilliseconds:F0} ms in every segment): {(failedLatency.Count == 0 ? "passed" : $"**failed** in {string.Join(", ", failedLatency)}")}"));
        md.AppendLine(Invariant($"- Wall-clock safety limit reached in any warmup run: {(rows.Any(r => r.CspTimeLimitReached == true) ? "**yes**" : "no")}"));
        md.AppendLine(Invariant($"- CSP fell back to the heuristic: {fellBack.Length} scenarios{(fellBack.Length == 0 ? string.Empty : $" ({string.Join(", ", fellBack)})")}"));
        md.AppendLine();

        md.AppendLine("## Limitations");
        md.AppendLine();
        md.AppendLine("- Synthetic, generator-only evidence: the matrix comes from memory, so no OpenRouteService, ranking or database");
        md.AppendLine("  latency is included, and conclusions generalize only to the tested synthetic distribution.");
        md.AppendLine("- One developer machine, no CPU pinning or isolation, a single process run sequentially. Sub-millisecond samples are");
        md.AppendLine("  dominated by timer and JIT noise, and with few measured iterations per scenario, p95 and max are sensitive to");
        md.AppendLine("  single outliers. Compare latency only between runs on the same host.");
        md.AppendLine("- CSP results are deterministic for a given input while no run reaches the wall-clock safety limit");
        md.AppendLine(Invariant($"  ({csp.TimeLimitMilliseconds} ms), so quality and outcome columns reproduce exactly on any host that stays under it;"));
        md.AppendLine("  latency columns do not. A host slow enough to reach the limit gets a different, host-dependent result (UC-10 CSP");
        md.AppendLine("  spec, Phase 2 R5).");
        md.AppendLine("- The CSP model does not contain rest stops. With `Auto` or `Frequent` rest, the solver searches a model that reserves");
        md.AppendLine("  rest time and the relaxed model without the reserve (one shared node budget), passes the best solutions of both");
        md.AppendLine("  through `ItineraryScheduleEvaluator` and keeps the schedule with the lowest evaluated objective. The schedule that");
        md.AppendLine("  is optimal after rest insertion can still lie outside the solutions it evaluates.");
        return md.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string WarmupOutcome(IReadOnlyList<SolverComparisonScenarioRow> rows, SolverComparisonTimingRow timing)
    {
        var row = rows.First(r => r.Scenario == timing.Scenario);
        return timing.Mode switch
        {
            DisabledMode => row.DisabledOutcome,
            HeuristicMode => row.HeuristicOutcome,
            _ => row.CspOutcome,
        };
    }

    private static void ValidateMetadata(SolverComparisonMetadata meta, List<string> problems)
    {
        if (meta.SourceHash is null || meta.SourceHash.Length != 64 || !meta.SourceHash.All(char.IsAsciiHexDigitLower))
        {
            problems.Add($"source hash '{meta.SourceHash}' is not a full 64-character lowercase SHA-256");
        }

        if (meta.SourceFileCount < 1)
        {
            problems.Add(Invariant($"the source hash must cover at least one file, found {meta.SourceFileCount}"));
        }

        foreach (var preference in meta.RestPreferences ?? [])
        {
            if (!IsRestPreference(preference))
            {
                problems.Add($"rest preference '{preference}' is not a RestPreference value");
            }
        }

        if (meta.WarmupIterations < 1)
        {
            problems.Add(Invariant($"warmup iterations must be at least 1 (the warmup run produces the compared plan), found {meta.WarmupIterations}"));
        }

        if (meta.MeasuredIterations < 1)
        {
            problems.Add(Invariant($"measured iterations must be at least 1, found {meta.MeasuredIterations}"));
        }

        if (meta.CandidateCounts is null || meta.MandatoryCounts is null || meta.RestPreferences is null || meta.Csp is null)
        {
            problems.Add("metadata must declare candidate_counts, mandatory_counts, rest_preferences and csp");
        }
    }

    private static bool IsRestPreference(string value) =>
        Enum.GetNames<RestPreference>().Contains(value, StringComparer.Ordinal);

    private static void ValidateScenario(SolverComparisonScenarioRow row, CspOptions csp, List<string> problems)
    {
        if (row.Segment.Length == 0)
        {
            problems.Add($"scenario {row.Scenario} has no segment");
        }

        if (!Verdicts.Contains(row.Verdict, StringComparer.Ordinal))
        {
            problems.Add($"scenario {row.Scenario} has unknown verdict '{row.Verdict}'");
        }

        if (!IsRestPreference(row.RestPreference))
        {
            problems.Add($"scenario {row.Scenario} has unknown rest preference '{row.RestPreference}'");
        }

        ValidateOutcome(row.Scenario, DisabledMode, row.DisabledOutcome, problems);
        ValidateOutcome(row.Scenario, HeuristicMode, row.HeuristicOutcome, problems);
        ValidateOutcome(row.Scenario, CspMode, row.CspOutcome, problems);
        if (row.CspOutcome == SchedulingSolverOutcomes.Csp && row.CspVisits > csp.MaxStops)
        {
            problems.Add(Invariant($"scenario {row.Scenario} is a CSP plan with {row.CspVisits} visits, above MaxStops {csp.MaxStops}"));
        }
    }

    private static void ValidateTiming(
        SolverComparisonTimingRow timing,
        int measuredIterations,
        Dictionary<string, string> segmentOf,
        List<string> problems)
    {
        var key = Invariant($"timing {timing.Scenario} / {timing.Mode} / iteration {timing.Iteration}");
        if (Modes.Contains(timing.Mode, StringComparer.Ordinal))
        {
            ValidateOutcome(key, timing.Mode, timing.Outcome, problems);
        }
        else
        {
            problems.Add($"{key} has unknown mode '{timing.Mode}'");
        }

        if (!double.IsFinite(timing.ElapsedMs) || timing.ElapsedMs < 0)
        {
            problems.Add(Invariant($"{key} has invalid elapsed time {timing.ElapsedMs}"));
        }

        if (!segmentOf.TryGetValue(timing.Scenario, out var segment))
        {
            problems.Add($"{key} belongs to no scenario");
        }
        else if (segment != timing.Segment)
        {
            problems.Add($"{key} is in segment '{timing.Segment}', but its scenario is in '{segment}'");
        }

        if (timing.Iteration < 1 || timing.Iteration > measuredIterations)
        {
            problems.Add(Invariant($"{key} is outside iterations 1–{measuredIterations}"));
        }
    }

    private static void ValidateOutcome(string subject, string mode, string outcome, List<string> problems)
    {
        var allowed = mode == CspMode ? CspModeOutcomes : HeuristicModeOutcomes;
        if (!allowed.Contains(outcome, StringComparer.Ordinal))
        {
            problems.Add($"{subject} has outcome '{outcome}', which the {mode} mode cannot produce");
        }
    }

    private static void Throw(List<string> problems)
    {
        if (problems.Count == 0)
        {
            return;
        }

        var shown = problems.Take(MaxReportedProblems);
        var more = problems.Count > MaxReportedProblems
            ? Invariant($"\n- … and {problems.Count - MaxReportedProblems} more")
            : string.Empty;
        throw new InvalidDataException("Invalid solver comparison data:\n- " + string.Join("\n- ", shown) + more);
    }

    private static double[] Samples(IEnumerable<SolverComparisonTimingRow> timings, string segment, string mode) =>
        timings.Where(t => t.Segment == segment && t.Mode == mode).Select(t => t.ElapsedMs).ToArray();

    private static string Summary(double[] samples) =>
        samples.Length == 0
            ? "–"
            : Invariant($"{Percentile(samples, 0.5):F2} / {Percentile(samples, 0.95):F2} / {samples.Max():F2}");

    private static double Average(IEnumerable<SolverComparisonScenarioRow> rows, Func<SolverComparisonScenarioRow, int?> value)
    {
        var values = rows.Select(value).Where(v => v is not null).Select(v => (double)v!.Value).ToArray();
        return values.Length == 0 ? 0 : values.Average();
    }

    private static List<T> ReadCsv<T>(string path, string header, Func<string[], T> parse)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || lines[0] != header)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} does not start with the expected header.");
        }

        var expectedColumns = header.Split(',').Length;
        var rows = new List<T>(lines.Length - 1);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var fields = line.Split(',');
            if (fields.Length != expectedColumns)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)}: expected {expectedColumns} columns in '{line}'.");
            }

            rows.Add(parse(fields));
        }

        return rows;
    }

    private static SolverComparisonScenarioRow ParseScenario(string[] f) => new(
        f[0], f[1], f[2], int.Parse(f[3], CultureInfo.InvariantCulture), f[4],
        NullableInt(f[5]), NullableInt(f[6]), NullableInt(f[7]), NullableInt(f[8]),
        NullableInt(f[9]), NullableInt(f[10]), NullableInt(f[11]), NullableInt(f[12]),
        f[13], f[14], f[15],
        NullableInt(f[16]), NullableInt(f[17]), NullableBool(f[18]), NullableBool(f[19]), NullableBool(f[20]));

    private static SolverComparisonTimingRow ParseTiming(string[] f) => new(
        f[0], f[1], f[2], int.Parse(f[3], CultureInfo.InvariantCulture),
        double.Parse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture), f[5]);

    private static int? NullableInt(string value) =>
        value.Length == 0 ? null : int.Parse(value, CultureInfo.InvariantCulture);

    private static bool? NullableBool(string value) =>
        value.Length == 0 ? null : bool.Parse(value);

    private static string Format(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Format(bool? value) =>
        value switch { true => "true", false => "false", null => string.Empty };

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}