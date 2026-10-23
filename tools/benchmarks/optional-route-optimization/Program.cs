using System.Diagnostics;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

Console.WriteLine("=== UC-10 Optional Route Optimization Benchmark Runner ===");
Console.WriteLine($"OS: {Environment.OSVersion}, Processor Count: {Environment.ProcessorCount}, .NET: {Environment.Version}");

var candidateCounts = new[] { 10, 20, 40 };
var mandatoryCounts = new[] { 0, 1, 3, 6 };

Console.WriteLine($"{"Cand",4} | {"Mand",4} | {"Base p95",10} | {"Opt p95",10} | {"Delta p95",10} | {"Ratio",6} | {"Base Travel",12} | {"Opt Travel",12} | {"Travel Saved",12}");
Console.WriteLine(new string('-', 95));

foreach (var candCount in candidateCounts)
{
    foreach (var mandCount in mandatoryCounts)
    {
        if (mandCount > candCount) continue;

        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candCount, mandCount);
        var provider = new BenchmarkRouteDurationProvider(matrix);

        var disabledService = new ItineraryGenerationService(
            provider,
            options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = false });

        var enabledService = new ItineraryGenerationService(
            provider,
            options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = true });

        // Warmup
        _ = await disabledService.GenerateAsync(input, CancellationToken.None);
        _ = await enabledService.GenerateAsync(input, CancellationToken.None);

        var iterations = 20;
        var disabledTimes = new List<double>(iterations);
        var enabledTimes = new List<double>(iterations);
        GeneratedItineraryPlan? disabledPlan = null;
        GeneratedItineraryPlan? enabledPlan = null;

        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            var res = await disabledService.GenerateAsync(input, CancellationToken.None);
            sw.Stop();
            disabledTimes.Add(sw.Elapsed.TotalMilliseconds);
            if (res.IsSuccess) disabledPlan = res.Value;
        }

        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            var res = await enabledService.GenerateAsync(input, CancellationToken.None);
            sw.Stop();
            enabledTimes.Add(sw.Elapsed.TotalMilliseconds);
            if (res.IsSuccess) enabledPlan = res.Value;
        }

        disabledTimes.Sort();
        enabledTimes.Sort();
        var baseP95 = disabledTimes[(int)(disabledTimes.Count * 0.95)];
        var optP95 = enabledTimes[(int)(enabledTimes.Count * 0.95)];
        var deltaP95 = optP95 - baseP95;
        var ratio = baseP95 > 0 ? optP95 / baseP95 : 0;

        var baseTravel = disabledPlan?.Items.Where(it => it.TravelDurationToNextMinutes.HasValue).Sum(it => it.TravelDurationToNextMinutes!.Value) ?? 0;
        var optTravel = enabledPlan?.Items.Where(it => it.TravelDurationToNextMinutes.HasValue).Sum(it => it.TravelDurationToNextMinutes!.Value) ?? 0;
        var travelSaved = baseTravel - optTravel;

        Console.WriteLine($"{candCount,4} | {mandCount,4} | {baseP95,8:F2}ms | {optP95,8:F2}ms | {deltaP95,8:F2}ms | {ratio,5:F2}x | {baseTravel,10}m | {optTravel,10}m | {travelSaved,10}m");
    }
}

Console.WriteLine("=== Benchmark completed ===");

internal sealed class BenchmarkRouteDurationProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
{
    public Task<RouteDurationMatrix> GetMatrixAsync(IReadOnlyList<RoutePoint> points, TransportMode transportMode, CancellationToken cancellationToken) =>
        Task.FromResult(matrix);
}
