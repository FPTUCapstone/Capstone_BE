using System.Diagnostics;
using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden;

public class SolverAllocationBaselineTests
{
    private static readonly SchedulingGenerationOptions Options = new();
    private static readonly CspOptions GoldenCspOptions = new() { TimeLimitMilliseconds = 120_000 };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public record ScenarioAllocationRecord(string ScenarioId, long AllocatedBytes);

    public record AllocationBaseline(
        DateTimeOffset CreatedAtUtc,
        IReadOnlyList<ScenarioAllocationRecord> Scenarios);

    [Fact]
    public void Record_or_verify_allocation_baseline()
    {
        var snapshotDir = GetSnapshotsDirectory();
        var baselineFile = Path.Combine(snapshotDir, "_allocation-baseline.json");
        var isUpdateMode = Environment.GetEnvironmentVariable("TRIPMATE_ALLOCATION_UPDATE") == "1"
                           || !File.Exists(baselineFile);

        var scenarios = GoldenScenarioFactory.CreateAllScenarios();
        var currentAllocations = new List<ScenarioAllocationRecord>();

        foreach (var scenario in scenarios)
        {
            var solver = new CspItinerarySolver(
                new ItineraryScheduleEvaluator(Options),
                Options,
                scenario.CspOptions ?? GoldenCspOptions);

            // Warmup JIT
            _ = solver.Solve(
                scenario.Input,
                scenario.Matrix,
                scenario.Candidates,
                scenario.Indices,
                scenario.InitialIncumbent);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = solver.Solve(
                scenario.Input,
                scenario.Matrix,
                scenario.Candidates,
                scenario.Indices,
                scenario.InitialIncumbent);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            currentAllocations.Add(new ScenarioAllocationRecord(scenario.Id, allocated));
        }

        if (isUpdateMode)
        {
            var baseline = new AllocationBaseline(DateTimeOffset.UtcNow, currentAllocations);
            File.WriteAllText(baselineFile, JsonSerializer.Serialize(baseline, JsonOptions));
            return;
        }

        var baselineContent = File.ReadAllText(baselineFile);
        var existingBaseline = JsonSerializer.Deserialize<AllocationBaseline>(baselineContent, JsonOptions)!;
        var baselineMap = existingBaseline.Scenarios.ToDictionary(s => s.ScenarioId, s => s.AllocatedBytes);

        foreach (var current in currentAllocations)
        {
            if (baselineMap.TryGetValue(current.ScenarioId, out var baselineBytes))
            {
                // Ngưỡng bảo vệ: Memory allocation <= 1.05 * baseline + 4KB
                var maxAllowed = (long)(baselineBytes * 1.05) + 4096;
                current.AllocatedBytes.Should().BeLessThanOrEqualTo(
                    maxAllowed,
                    $"Kịch bản {current.ScenarioId} không được thoái lui cấp phát bộ nhớ so với baseline ({current.AllocatedBytes:N0} bytes > {maxAllowed:N0} bytes)");
            }
        }
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