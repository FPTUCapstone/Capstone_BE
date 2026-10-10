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
        foreach (var baseName in CommittedReportNames())
        {
            data.Add(baseName);
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
    public void CommittedReport_IsValid(string baseName)
    {
        // Read validates the data (see SolverComparisonReport.Validate) and throws on any malformed or surplus row.
        var data = SolverComparisonReport.Read(BenchmarksDirectory(), baseName);

        data.Timings.Should().HaveCount(data.Scenarios.Count * 3 * data.Metadata.MeasuredIterations);
    }

    /// <summary>
    /// Báo cáo mới nhất phải đo đúng mã nguồn đang commit. So bằng hash nội dung nên không cần <c>.git</c> và không
    /// phụ thuộc kiểu merge (squash, rebase hay merge commit). Báo cáo cũ hơn là lịch sử, không bắt buộc khớp.
    /// </summary>
    [Fact]
    public void LatestCommittedReport_MeasuredTheCurrentSource()
    {
        // Tên tệp chứa ngày dạng yyyy-MM-dd nên thứ tự ordinal cũng là thứ tự thời gian.
        var latest = CommittedReportNames().LastOrDefault();
        latest.Should().NotBeNull();
        var meta = SolverComparisonReport.Read(BenchmarksDirectory(), latest!).Metadata;
        var current = SolverComparisonReport.ComputeSourceHash(RepositoryRoot());

        var ensure = () => SolverComparisonReport.EnsureMatchesSource(meta, current.Hash);

        ensure.Should().NotThrow();
        meta.SourceFileCount.Should().Be(current.FileCount);
    }

    [Fact]
    public void EnsureMatchesSource_RejectsAReportOfDifferentSource()
    {
        var ensure = () => SolverComparisonReport.EnsureMatchesSource(SampleData().Metadata, new string('b', 64));

        ensure.Should().Throw<InvalidDataException>().WithMessage("*now hash to bbbb*Regenerate it*");
    }

    [Fact]
    public void ComputeSourceHash_IgnoresLineEndings_AndTracksContentAndPaths()
    {
        var root = TemporaryDirectory();
        try
        {
            string[] inputs = ["src/Solver", "tools/Program.cs"];
            Directory.CreateDirectory(Path.Combine(root, "src", "Solver", "Nested"));
            Directory.CreateDirectory(Path.Combine(root, "tools"));
            File.WriteAllText(Path.Combine(root, "src", "Solver", "A.cs"), "class A\n{\n}\n");
            File.WriteAllText(Path.Combine(root, "src", "Solver", "Nested", "B.cs"), "class B { }\n");
            File.WriteAllText(Path.Combine(root, "src", "Solver", "notes.md"), "not source\n");
            File.WriteAllText(Path.Combine(root, "tools", "Program.cs"), "return 0;\n");
            var original = SolverComparisonReport.ComputeSourceHash(root, inputs);

            File.WriteAllText(Path.Combine(root, "src", "Solver", "A.cs"), "class A\r\n{\r\n}\r\n");
            File.WriteAllText(Path.Combine(root, "src", "Solver", "notes.md"), "changed, still not source\n");
            var crlf = SolverComparisonReport.ComputeSourceHash(root, inputs);

            File.WriteAllText(Path.Combine(root, "src", "Solver", "Nested", "B.cs"), "class B { int x; }\n");
            var edited = SolverComparisonReport.ComputeSourceHash(root, inputs);

            original.FileCount.Should().Be(3);
            original.Hash.Should().MatchRegex("^[0-9a-f]{64}$");
            crlf.Should().Be(original);
            edited.Hash.Should().NotBe(original.Hash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ComputeSourceHash_RejectsAMissingInput()
    {
        var compute = () => SolverComparisonReport.ComputeSourceHash(RepositoryRoot(), ["src/DoesNotExist"]);

        compute.Should().Throw<FileNotFoundException>().WithMessage("*src/DoesNotExist*");
    }

    // Mỗi trường hợp làm hỏng tệp .timings.csv của một bộ dữ liệu hợp lệ theo một cách, và Read phải từ chối.
    [Theory]
    [InlineData("unknown-mode", "*unknown mode 'bogus'*")]
    [InlineData("unknown-outcome", "*outcome 'bogus'*")]
    [InlineData("outcome-of-another-mode", "*outcome 'csp', which the disabled mode cannot produce*")]
    [InlineData("negative-elapsed", "*invalid elapsed time -1*")]
    [InlineData("nan-elapsed", "*invalid elapsed time NaN*")]
    [InlineData("infinite-elapsed", "*invalid elapsed time Infinity*")]
    [InlineData("extra-row-for-unknown-scenario", "*c10-m0-s2 / csp / iteration 1 belongs to no scenario*")]
    [InlineData("duplicate-key", "*c10-m0-s1 / disabled / iteration 1 appears more than once*")]
    [InlineData("mismatched-segment", "*is in segment 'c20-m0', but its scenario is in 'c10-m0'*")]
    [InlineData("missing-iteration", "*c10-m0-s1 / csp / iteration 2 is missing*")]
    [InlineData("iteration-zero", "*iteration 0 is outside iterations 1–2*")]
    [InlineData("iteration-above-measured", "*iteration 3 is outside iterations 1–2*")]
    public void Read_RejectsMalformedTimingData(string mutation, string expectedMessage)
    {
        var directory = TemporaryDirectory();
        try
        {
            SolverComparisonReport.Write(directory, SampleData());
            var baseName = SolverComparisonReport.BaseName(SampleDate);
            var path = Path.Combine(directory, baseName + ".timings.csv");
            var lines = File.ReadAllLines(path).Where(line => line.Length > 0).ToList();
            MutateTimings(lines, mutation);
            File.WriteAllLines(path, lines);

            var read = () => SolverComparisonReport.Read(directory, baseName);

            read.Should().Throw<InvalidDataException>().WithMessage(expectedMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown-verdict", "*unknown verdict 'bogus'*")]
    [InlineData("unknown-scenario-outcome", "*c10-m0-s1 has outcome 'bogus', which the csp mode cannot produce*")]
    [InlineData("csp-plan-above-max-stops", "*CSP plan with 4 visits, above MaxStops 3*")]
    [InlineData("duplicate-scenario", "*scenario c10-m0-s1 appears more than once*")]
    [InlineData("corpus-size-mismatch", "*expected 2 scenarios for the declared corpus, found 1*")]
    [InlineData("rest-preferences-in-corpus-size", "*expected 2 scenarios for the declared corpus, found 1*")]
    [InlineData("short-source-hash", "*source hash 'abc123' is not a full 64-character lowercase SHA-256*")]
    [InlineData("no-source-files", "*must cover at least one file, found 0*")]
    [InlineData("unknown-declared-rest-preference", "*rest preference 'Sometimes' is not a RestPreference value*")]
    [InlineData("unknown-scenario-rest-preference", "*c10-m0-s1 has unknown rest preference 'Sometimes'*")]
    [InlineData("no-measured-iterations", "*measured iterations must be at least 1, found 0*")]
    public void Validate_RejectsMalformedScenariosAndMetadata(string mutation, string expectedMessage)
    {
        var data = SampleData();
        var row = data.Scenarios[0];
        data = mutation switch
        {
            "unknown-verdict" => data with { Scenarios = [row with { Verdict = "bogus" }] },
            "unknown-scenario-outcome" => data with { Scenarios = [row with { CspOutcome = "bogus" }] },
            "csp-plan-above-max-stops" => data with { Scenarios = [row with { CspVisits = 4 }] },
            "duplicate-scenario" => data with { Scenarios = [row, row] },
            "corpus-size-mismatch" => data with { Metadata = data.Metadata with { Seeds = 2 } },
            "rest-preferences-in-corpus-size" => data with { Metadata = data.Metadata with { RestPreferences = ["None", "Auto"] } },
            "short-source-hash" => data with { Metadata = data.Metadata with { SourceHash = "abc123" } },
            "no-source-files" => data with { Metadata = data.Metadata with { SourceFileCount = 0 } },
            "unknown-declared-rest-preference" => data with { Metadata = data.Metadata with { RestPreferences = ["Sometimes"] } },
            "unknown-scenario-rest-preference" => data with { Scenarios = [row with { RestPreference = "Sometimes" }] },
            "no-measured-iterations" => data with { Metadata = data.Metadata with { MeasuredIterations = 0 } },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };

        var validate = () => SolverComparisonReport.Validate(data);

        validate.Should().Throw<InvalidDataException>().WithMessage(expectedMessage);
        FluentActions.Invoking(() => SolverComparisonReport.Markdown(data)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Read_RejectsAMetadataDateThatDoesNotMatchTheFileName()
    {
        var directory = TemporaryDirectory();
        try
        {
            SolverComparisonReport.Write(directory, SampleData());
            var source = SolverComparisonReport.BaseName(SampleDate);
            var renamed = SolverComparisonReport.BaseName("2026-01-03");
            foreach (var suffix in new[] { ".csv", ".timings.csv", ".meta.json" })
            {
                File.Move(Path.Combine(directory, source + suffix), Path.Combine(directory, renamed + suffix));
            }

            var read = () => SolverComparisonReport.Read(directory, renamed);

            read.Should().Throw<InvalidDataException>().WithMessage("*dated 2026-01-02*");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Write_ThenRead_RoundTripsTheDataAndTheReport()
    {
        var directory = TemporaryDirectory();
        var data = SampleData();

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
            markdown.Should().Contain("| None | 1 | 1 | 0 | 0 | 0 |");
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

    private const string SampleDate = "2026-01-02";

    // Một scenario, hai lần đo mỗi chế độ: đủ để mọi quy tắc kiểm tra đều có dữ liệu hợp lệ để làm hỏng.
    private static SolverComparisonData SampleData() => new(
        new SolverComparisonMetadata(
            SampleDate, new string('a', 64), 12, "2026-01-02T03:04:05Z", "TestOS", "TestCPU", 4, ".NET 10", "Release",
            1, 2, 1, [10], [0], ["None"], 0, 40, new CspOptions { MaxStops = 3 }),
        [
            new SolverComparisonScenarioRow(
                "c10-m0", "c10-m0-s1", "None", 0, "better", 3, 3, 3, 3, 90, 80, 300, 290,
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

    // lines[0] là header; các dòng dữ liệu theo thứ tự của SampleData (disabled 1, 2, heuristic 1, 2, csp 1, 2).
    private static void MutateTimings(List<string> lines, string mutation)
    {
        static string SetField(string line, int index, string value)
        {
            var fields = line.Split(',');
            fields[index] = value;
            return string.Join(',', fields);
        }

        switch (mutation)
        {
            case "unknown-mode":
                // A surplus row with a unique key: before validation it was silently dropped from every statistic.
                lines.Add("c10-m0,c10-m0-s1,bogus,1,1.0000,heuristic");
                break;
            case "unknown-outcome":
                lines[3] = SetField(lines[3], 5, "bogus");
                break;
            case "outcome-of-another-mode":
                lines[1] = SetField(lines[1], 5, SchedulingSolverOutcomes.Csp);
                break;
            case "negative-elapsed":
                lines[1] = SetField(lines[1], 4, "-1.0000");
                break;
            case "nan-elapsed":
                lines[1] = SetField(lines[1], 4, "NaN");
                break;
            case "infinite-elapsed":
                lines[1] = SetField(lines[1], 4, "Infinity");
                break;
            case "extra-row-for-unknown-scenario":
                lines.Add("c10-m0,c10-m0-s2,csp,1,1.0000,csp");
                break;
            case "duplicate-key":
                lines.Add(lines[1]);
                break;
            case "mismatched-segment":
                lines[1] = SetField(lines[1], 0, "c20-m0");
                break;
            case "missing-iteration":
                lines.RemoveAt(6);
                break;
            case "iteration-zero":
                lines[1] = SetField(lines[1], 3, "0");
                break;
            case "iteration-above-measured":
                lines[1] = SetField(lines[1], 3, "3");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static string[] CommittedReportNames() =>
        Directory.GetFiles(BenchmarksDirectory(), ReportPrefix + "*.meta.json")
            .Select(meta => Path.GetFileName(meta)[..^".meta.json".Length])
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string TemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), "solver-comparison-" + Guid.NewGuid().ToString("N"));

    private static string BenchmarksDirectory() => Path.Combine(RepositoryRoot(), "docs", "benchmarks");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("could not locate the repository root (TripMate.slnx) from the test runtime directory");
        return directory!.FullName;
    }
}