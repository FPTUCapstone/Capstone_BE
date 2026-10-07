using System.Text.Json;

using FluentAssertions;

namespace TripMate.CandidatePoolReplay.Tests;

public sealed class ReplayEngineTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Run_BaselineIsDeterministicAndExperimentalStrategiesStayWithinFrozenPool()
    {
        ReplayManifest manifest = Manifest();
        ReplayScenario scenario = Scenario();
        string json = JsonSerializer.Serialize(scenario, JsonOptions);

        ReplayOutput first = ReplayEngine.Run(json, manifest);
        ReplayOutput second = ReplayEngine.Run(json, manifest);

        ReplayEngine.Serialize(first).Should().Be(ReplayEngine.Serialize(second));
        StrategyReplayResult baseline = first.Scenarios.Single().Strategies.Single(item => item.Strategy == "A-current");
        StrategyReplayResult backfill = first.Scenarios.Single().Strategies.Single(item => item.Strategy == "B-frozen-overflow");
        StrategyReplayResult diversity = first.Scenarios.Single().Strategies.Single(item => item.Strategy == "C-desirability-diversity");
        StrategyReplayResult sensitivity = first.Scenarios.Single().Strategies.Single(item => item.Strategy == "D-cap-sensitivity");
        baseline.ProviderPool.Should().Equal("c1", "c2", "c3");
        baseline.MatrixSelection.Should().Equal("c1", "c2");
        baseline.PlannedOptional.Should().Equal("c1");
        baseline.ProviderPool.Should().NotContain("c4");
        baseline.MatrixSelection.Should().NotContain("c3");
        baseline.MatrixElements.Should().Be(16);
        backfill.PlannedOptional.Should().Equal("c1", "c3");
        backfill.MatrixSelection.Should().Equal("c1", "c3");
        backfill.MatrixElements.Should().Be(16);
        backfill.PairedDelta!.BaseUtilitySum.Should().Be(80);
        backfill.PairedDelta.VisitSetChanged.Should().BeTrue();
        backfill.PlannedOptional.Should().OnlyContain(id => backfill.ProviderPool.Contains(id));
        diversity.PlannedOptional.Should().HaveCount(2).And.Contain("c3");
        sensitivity.ProviderPool.Should().Equal("c1", "c2", "c3", "c4");
        sensitivity.MatrixSelection.Should().Equal("c1", "c2", "c3");
        sensitivity.PlannedOptional.Should().Equal("c1", "c3");
        sensitivity.MatrixSelection.Should().NotContain("c4");
        sensitivity.MatrixElements.Should().Be(25);
    }

    [Fact]
    public void Run_ShuffledScenarioAndCandidateOrderProducesSameNormalizedResult()
    {
        ReplayScenario first = Scenario();
        ReplayScenario second = first with { Candidates = first.Candidates.Reverse().ToArray() };

        ReplayOutput original = ReplayEngine.Run(
            string.Join('\n', JsonSerializer.Serialize(first, JsonOptions), JsonSerializer.Serialize(first with { ScenarioId = "s2" }, JsonOptions)),
            Manifest());
        ReplayOutput shuffled = ReplayEngine.Run(
            string.Join('\n', JsonSerializer.Serialize(second with { ScenarioId = "s2" }, JsonOptions), JsonSerializer.Serialize(second, JsonOptions)),
            Manifest());

        original.Scenarios.Should().BeEquivalentTo(shuffled.Scenarios, options => options.WithStrictOrdering());
        original.CorpusSha256.Should().Be(shuffled.CorpusSha256);
    }

    [Fact]
    public void RunMeasured_SeparatesNondeterministicTimingFromDeterministicResults()
    {
        string json = JsonSerializer.Serialize(Scenario(), JsonOptions);

        ReplayMeasuredOutput measured = ReplayEngine.RunMeasured(json, Manifest());
        ReplayOutput deterministic = ReplayEngine.Run(json, Manifest());

        ReplayEngine.Serialize(measured.Output).Should().Be(ReplayEngine.Serialize(deterministic));
        measured.Timing.Strategies.Should().HaveCount(8);
        measured.Timing.Strategies.Should().OnlyContain(strategy =>
            strategy.SampleCount == 1
            && strategy.P50Microseconds >= 0
            && strategy.P95Microseconds >= 0
            && strategy.P99Microseconds >= 0
            && strategy.MeanAllocatedBytes >= 0);
        measured.Timing.Strategies.Select(strategy => strategy.Stage)
            .Should().OnlyContain(stage => stage == "selection" || stage == "generation");
        ReplayEngine.Serialize(measured.Output).Should().NotContain("generatedAtUtc");
        ReplayEngine.SerializeTiming(measured.Timing).Should().Contain("generatedAtUtc");
    }

    [Fact]
    public void Run_RejectsUnknownSchemaDuplicateIdsAndIncompleteMatrix()
    {
        Action schema = () => ReplayEngine.Run(
            JsonSerializer.Serialize(Scenario() with { SchemaVersion = "2.0" }, JsonOptions),
            Manifest());
        Action duplicate = () => ReplayEngine.Run(
            JsonSerializer.Serialize(Scenario() with
            {
                Candidates = [.. Scenario().Candidates, Scenario().Candidates[0]],
            }, JsonOptions),
            Manifest());
        Action matrix = () => ReplayEngine.Run(
            JsonSerializer.Serialize(Scenario() with { RouteMinutes = [[0]] }, JsonOptions),
            Manifest());

        schema.Should().Throw<ReplayValidationException>();
        duplicate.Should().Throw<ReplayValidationException>().WithMessage("*duplicate candidate ID*");
        matrix.Should().Throw<ReplayValidationException>().WithMessage("*complete, square*");
    }

    [Fact]
    public void Run_RejectsManifestWhoseSensitivityCapsDoNotContainBaselineCaps()
    {
        ReplayManifest invalid = Manifest() with
        {
            SensitivityProviderCap = Manifest().ProviderCap - 1,
        };

        Action act = () => ReplayEngine.Run(
            JsonSerializer.Serialize(Scenario(), JsonOptions),
            invalid);

        act.Should().Throw<ReplayValidationException>()
            .WithMessage("*cap and diversity options are inconsistent*");
    }

    [Fact]
    public void Run_RejectsRawIdentityFields()
    {
        string json = JsonSerializer.Serialize(Scenario(), JsonOptions);
        json = json.Replace("\"category\":", "\"poiName\":\"secret\",\"category\":", StringComparison.Ordinal);

        Action act = () => ReplayEngine.Run(json, Manifest());

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task Cli_InvalidInputReturnsNonzeroWithoutPartialOutput()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tripmate-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "input.jsonl");
            string manifest = Path.Combine(directory, "manifest.json");
            string output = Path.Combine(directory, "result.json");
            await File.WriteAllTextAsync(input, "{ invalid json }");
            await File.WriteAllTextAsync(
                manifest,
                JsonSerializer.Serialize(Manifest(), JsonOptions));
            using var error = new StringWriter();

            int exitCode = await ReplayCli.RunAsync(
                ["--input", input, "--manifest", manifest, "--output", output],
                error);

            exitCode.Should().Be(1);
            File.Exists(output).Should().BeFalse();
            File.Exists(output + ".tmp").Should().BeFalse();
            error.ToString().Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_WithTimingOutput_WritesDeterministicResultAndSeparateTimingMetadata()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tripmate-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "input.jsonl");
            string manifest = Path.Combine(directory, "manifest.json");
            string output = Path.Combine(directory, "result.json");
            string timing = Path.Combine(directory, "timing.json");
            await File.WriteAllTextAsync(
                input,
                JsonSerializer.Serialize(Scenario(), JsonOptions));
            await File.WriteAllTextAsync(
                manifest,
                JsonSerializer.Serialize(Manifest(), JsonOptions));
            using var error = new StringWriter();

            int exitCode = await ReplayCli.RunAsync(
                [
                    "--input", input,
                    "--manifest", manifest,
                    "--output", output,
                    "--timing-output", timing,
                ],
                error);

            exitCode.Should().Be(0);
            File.Exists(output).Should().BeTrue();
            File.Exists(timing).Should().BeTrue();
            (await File.ReadAllTextAsync(output)).Should().NotContain("generatedAtUtc");
            (await File.ReadAllTextAsync(timing)).Should().Contain("generatedAtUtc");
            error.ToString().Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ReplayManifest Manifest() => new(
        ReplayEngine.SchemaVersion,
        "test-fixture",
        "exploratory",
        ProviderCap: 3,
        MatrixCap: 2,
        DiversityCoreSize: 1,
        DiversityReservedSlots: 1,
        CategoryDiversityWeight: 1,
        GeoDiversityWeight: 1,
        GeoCellDefinition: "synthetic-cell-v1",
        BackfillOrder: "frozen-provider-order",
        SensitivityProviderCap: 4,
        SensitivityMatrixCap: 3);

    private static ReplayScenario Scenario()
    {
        ReplayCandidate[] candidates =
        [
            new("c1", "culture", "g1", 100, 100, 5, 5, 1, 10, true, 60, AllDay()),
            new("c2", "culture", "g1", 90, 90, 4, 4, 2, 20, false, 60, AllDay()),
            new("c3", "nature", "g2", 80, 80, 3, 3, 3, 30, true, 60, AllDay()),
            new("c4", "food", "g3", 70, 70, 2, 2, 4, 40, true, 60, AllDay()),
        ];
        string[] order = ["start", .. candidates.Select(item => item.Id), "end"];
        IReadOnlyList<IReadOnlyList<int>> matrix = Enumerable.Range(0, order.Length)
            .Select(row => (IReadOnlyList<int>)Enumerable.Range(0, order.Length)
                .Select(column => row == column ? 0 : Math.Abs(row - column) + 1)
                .ToArray())
            .ToArray();
        return new ReplayScenario(
            ReplayEngine.SchemaVersion,
            "s1",
            "car",
            "dense",
            new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero),
            "Asia/Ho_Chi_Minh",
            480,
            null,
            "none",
            [],
            candidates,
            order,
            matrix);
    }

    private static ReplayOpeningHours[] AllDay() => Enumerable.Range(0, 7)
        .Select(day => new ReplayOpeningHours((byte)day, new TimeOnly(0, 0), new TimeOnly(23, 59)))
        .ToArray();
}