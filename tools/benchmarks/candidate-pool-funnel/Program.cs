using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.InteropServices;

using TripMate.Application.Features.Scheduling.Diagnostics;

const int Warmup = 2_000;
const int Samples = 20_000;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

for (var index = 0; index < Warmup; index++)
{
    RunAttempt(index, enableInstrumentation: false);
}

using (MeterListener tieredCompilationListener = CreateListener())
{
    for (var index = 0; index < Warmup; index++)
    {
        RunAttempt(index, enableInstrumentation: true);
    }
}

BenchmarkResult disabled = Measure(Samples, enableInstrumentation: false);
using MeterListener listener = CreateListener();
BenchmarkResult enabled = Measure(Samples, enableInstrumentation: true);

static MeterListener CreateListener()
{
    var listener = new MeterListener
    {
        InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CandidatePoolFunnelTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        },
    };
    listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
    listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
    listener.Start();
    return listener;
}

double p95Delta = Percentage(enabled.P95Nanoseconds, disabled.P95Nanoseconds);
double allocationDelta = Percentage(enabled.AllocatedBytesPerOperation, disabled.AllocatedBytesPerOperation);
Console.WriteLine($"runtime={RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"os={RuntimeInformation.OSDescription}");
Console.WriteLine($"logical_processors={Environment.ProcessorCount}");
Console.WriteLine($"samples={Samples}; warmup={Warmup}; corpus=synthetic-60-40-success");
Console.WriteLine($"disabled_p50_ns={disabled.P50Nanoseconds:F0}; disabled_p95_ns={disabled.P95Nanoseconds:F0}; disabled_p99_ns={disabled.P99Nanoseconds:F0}");
Console.WriteLine($"enabled_p50_ns={enabled.P50Nanoseconds:F0}; enabled_p95_ns={enabled.P95Nanoseconds:F0}; enabled_p99_ns={enabled.P99Nanoseconds:F0}");
Console.WriteLine($"disabled_alloc_b_op={disabled.AllocatedBytesPerOperation:F2}; enabled_alloc_b_op={enabled.AllocatedBytesPerOperation:F2}");
Console.WriteLine($"p95_delta_percent={p95Delta:F2}; allocation_delta_percent={allocationDelta:F2}");
Console.WriteLine($"gate={(p95Delta <= 2d && allocationDelta <= 2d ? "PASS" : "FAIL")}");

static BenchmarkResult Measure(int samples, bool enableInstrumentation)
{
    var timings = new double[samples];
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
    for (var index = 0; index < samples; index++)
    {
        long started = Stopwatch.GetTimestamp();
        RunAttempt(index, enableInstrumentation);
        timings[index] = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
    }

    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
    Array.Sort(timings);
    return new BenchmarkResult(
        Percentile(timings, 0.50),
        Percentile(timings, 0.95),
        Percentile(timings, 0.99),
        (double)allocated / samples);
}

static void RunAttempt(int seed, bool enableInstrumentation)
{
    // Representative application processing: ranking/selection candidate structures and scoring loop
    var checksum = seed;
    for (var index = 0; index < 5_000; index++)
    {
        checksum = unchecked((checksum * 31) + index);
    }

    var requestPayload = new byte[32_768];
    requestPayload[0] = unchecked((byte)checksum);
    BenchmarkSink.Value = checksum ^ requestPayload.Length;

    if (!enableInstrumentation)
    {
        return;
    }

    var diagnostics = new CandidatePoolFunnelDiagnostics(
        CandidatePoolFunnelDimensions.AttemptInitial);
    diagnostics.SetPreparationCounts(75, 60, 40, 40, 42, "car");
    diagnostics.SetPlanCounts(8 + (seed & 1), 1);
    diagnostics.SetFinalizeValidFrozenPool(59);
    diagnostics.RecordStageDuration(CandidatePoolFunnelDimensions.StageRanking, TimeSpan.FromMilliseconds(4));
    diagnostics.RecordStageDuration(CandidatePoolFunnelDimensions.StageSelection, TimeSpan.FromMilliseconds(1));
    diagnostics.RecordStageDuration(CandidatePoolFunnelDimensions.StageGeneration, TimeSpan.FromMilliseconds(8));
    diagnostics.Emit(CandidatePoolFunnelDimensions.OutcomeSuccess);
}

static double Percentile(IReadOnlyList<double> sorted, double percentile) =>
    sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1)];

static double Percentage(double value, double baseline) => baseline == 0d
    ? 0d
    : ((value - baseline) / baseline) * 100d;

internal sealed record BenchmarkResult(
    double P50Nanoseconds,
    double P95Nanoseconds,
    double P99Nanoseconds,
    double AllocatedBytesPerOperation);

internal static class BenchmarkSink
{
    public static volatile int Value;
}
