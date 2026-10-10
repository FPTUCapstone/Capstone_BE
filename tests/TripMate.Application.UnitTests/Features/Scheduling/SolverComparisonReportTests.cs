using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;

namespace TripMate.Application.UnitTests.Features.Scheduling;

/// <summary>
/// Bằng chứng benchmark trong <c>docs/benchmarks</c> phải dựng lại được từ chính dữ liệu đã commit: báo cáo
/// Markdown khớp từng chữ với bản dựng lại từ ba tệp dữ liệu, và dữ liệu đủ, đúng corpus, đúng giới hạn CSP.
/// </summary>
public class SolverComparisonReportTests
{
    private const string ReportPrefix = "UC-10-csp-solver-comparison-";

    public static TheoryData<string> CommittedReports()
    {
        var data = new TheoryData<string>();
        foreach (var meta in Directory.GetFiles(BenchmarksDirectory(), ReportPrefix + "*.meta.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(meta)[..^".meta.json".Length]);
        }

        return data;
    }

    [Fact]
    public void EveryCommittedReport_HasItsDataFiles()
    {
        var directory = BenchmarksDirectory();
        var reports = Directory.GetFiles(directory, ReportPrefix + "*.md");

        reports.Should().NotBeEmpty();
        foreach (var report in reports)
        {
            var baseName = Path.GetFileNameWithoutExtension(report);
            foreach (var suffix in new[] { ".csv", ".timings.csv", ".meta.json" })
            {
                File.Exists(Path.Combine(directory, baseName + suffix)).Should().BeTrue(
                    $"{baseName}.md must be reproducible from {baseName}{suffix}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(CommittedReports))]
    public void CommittedReport_MatchesTheReportRecomputedFromItsDataFiles(string baseName)
    {
        var directory = BenchmarksDirectory();
        var data = SolverComparisonReport.Read(directory, baseName);
        var committed = File.ReadAllText(Path.Combine(directory, baseName + ".md")).Replace("\r\n", "\n", StringComparison.Ordinal);

        SolverComparisonReport.Markdown(data).Should().Be(committed);
    }

    [Theory]
    [MemberData(nameof(CommittedReports))]
    public void CommittedReport_DataIsCompleteAndConsistent(string baseName)
    {
        var data = SolverComparisonReport.Read(BenchmarksDirectory(), baseName);
        var meta = data.Metadata;

        meta.CommitSha.Should().MatchRegex("^[0-9a-f]{40}$", "the report must name the full source commit");
        data.Scenarios.Should().HaveCount(
            meta.NamedScenarioCount + (meta.CandidateCounts.Count * meta.MandatoryCounts.Count * meta.Seeds));
        data.Scenarios.Select(r => r.Scenario).Should().OnlyHaveUniqueItems();

        foreach (var row in data.Scenarios)
        {
            foreach (var mode in new[] { SolverComparisonReport.DisabledMode, SolverComparisonReport.HeuristicMode, SolverComparisonReport.CspMode })
            {
                data.Timings
                    .Where(t => t.Scenario == row.Scenario && t.Mode == mode)
                    .Select(t => t.Iteration)
                    .Should().BeEquivalentTo(Enumerable.Range(1, meta.MeasuredIterations), $"{row.Scenario} / {mode}");
            }

            row.Verdict.Should().BeOneOf("better", "equal", "worse", "excluded");
            if (row.CspOutcome == SchedulingSolverOutcomes.Csp)
            {
                row.CspVisits.Should().BeLessThanOrEqualTo(meta.Csp.MaxStops, $"{row.Scenario} is a CSP plan");
            }
        }

        data.Timings.Select(t => t.Segment)
            .Should().BeSubsetOf(data.Scenarios.Select(r => r.Segment));
    }

    [Fact]
    public void Write_ThenRead_RoundTripsTheDataAndTheReport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "solver-comparison-" + Guid.NewGuid().ToString("N"));
        var data = new SolverComparisonData(
            new SolverComparisonMetadata(
                "2026-01-02", new string('a', 40), false, "2026-01-02T03:04:05Z", "TestOS", "TestCPU", 4, ".NET 10", "Release",
                1, 2, 1, [10], [0], 0, 40, new CspOptions { MaxStops = 3 }),
            [
                new SolverComparisonScenarioRow(
                    "c10-m0", "c10-m0-s1", 0, "better", 3, 3, 3, 3, 90, 80, 300, 290,
                    SchedulingSolverOutcomes.Heuristic, SchedulingSolverOutcomes.Heuristic, SchedulingSolverOutcomes.Csp,
                    42, 2, true, false, false),
            ],
            [
                new SolverComparisonTimingRow("c10-m0", "c10-m0-s1", SolverComparisonReport.DisabledMode, 1, 0.1234, SchedulingSolverOutcomes.Heuristic),
                new SolverComparisonTimingRow("c10-m0", "c10-m0-s1", SolverComparisonReport.DisabledMode, 2, 0.2, SchedulingSolverOutcomes.Heuristic),
                new SolverComparisonTimingRow("c10-m0", "c10-m0-s1", SolverComparisonReport.HeuristicMode, 1, 1.5, SchedulingSolverOutcomes.Heuristic),
                new SolverComparisonTimingRow("c10-m0", "c10-m0-s1", SolverComparisonReport.HeuristicMode, 2, 1.25, SchedulingSolverOutcomes.Heuristic),
                new SolverComparisonTimingRow("c10-m0", "c10-m0-s1", SolverComparisonReport.CspMode, 1, 12.5, SchedulingSolverOutcomes.Csp),
                new SolverComparisonTimingRow("c10-m0", "c10-m0-s1", SolverComparisonReport.CspMode, 2, 400.75, SchedulingSolverOutcomes.HeuristicFallbackCspSearchLimit),
            ]);

        try
        {
            SolverComparisonReport.Write(directory, data);
            var read = SolverComparisonReport.Read(directory, SolverComparisonReport.BaseName("2026-01-02"));
            var markdown = File.ReadAllText(Path.Combine(directory, SolverComparisonReport.BaseName("2026-01-02") + ".md"));

            read.Scenarios.Should().Equal(data.Scenarios);
            read.Timings.Should().Equal(data.Timings);
            read.Metadata.Should().BeEquivalentTo(data.Metadata);
            markdown.Should().Be(SolverComparisonReport.Markdown(read));
            markdown.Should().Contain("| c10-m0 | 1 | 1 | 0 | 0 |");
            markdown.Should().Contain("plans with more than MaxStops (3) visits: 0");
            markdown.Should().Contain("Measured runs whose outcome differs from the warmup run of the same scenario and mode: 1.");
            markdown.Should().Contain("**failed** in c10-m0");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(new[] { 5.0, 1.0, 3.0, 2.0, 4.0 }, 0.5, 3.0)]
    [InlineData(new[] { 5.0, 1.0, 3.0, 2.0, 4.0 }, 0.95, 5.0)]
    [InlineData(new[] { 7.0 }, 0.95, 7.0)]
    [InlineData(new double[0], 0.95, 0.0)]
    public void Percentile_UsesTheDocumentedNearestRankIndex(double[] samples, double percentile, double expected)
    {
        SolverComparisonReport.Percentile(samples, percentile).Should().Be(expected);
    }

    private static string BenchmarksDirectory()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("could not locate the repository root (TripMate.slnx) from the test runtime directory");
        return Path.Combine(directory!.FullName, "docs", "benchmarks");
    }
}