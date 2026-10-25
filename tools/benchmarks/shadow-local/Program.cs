using System.Runtime.InteropServices;

using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

var arguments = ParseArguments(args);
var outputPath = ShadowBenchmarkOutputPath.ValidateV3b(Required(arguments, "output"));
var measuredIterations = ParsePositive(arguments, "samples", defaultValue: 3);
var warmupIterations = ParseNonNegative(arguments, "warmup", defaultValue: 1);

var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
var scenarios = SchedulingBenchmarkScenarioFactory.CreateV3bScenarios(cityData);
var results = await ShadowBenchmarkExecutor.RunAsync(
    scenarios,
    cityData.Personas,
    warmupIterations,
    measuredIterations);
var baselineData = SchedulingBenchmarkScenarioFactory.CreateDaNangBaselineData();
var baselineResults = await ShadowBenchmarkExecutor.RunAsync(
    [SchedulingBenchmarkScenarioFactory.CreateScenario(baselineData)],
    baselineData.Personas,
    warmupIterations: 0,
    measuredIterations: 1);
var seedComparisons = ShadowBenchmarkComparison.CompareSeedExtension(baselineResults, results);
var metadata = new ShadowBenchmarkReportMetadata(
    DateTimeOffset.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    "Release",
    RuntimeInformation.OSDescription,
    RuntimeInformation.FrameworkDescription,
    Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? Environment.MachineName,
    Environment.ProcessorCount,
    warmupIterations);
var markdown = ShadowBenchmarkMarkdownRenderer.Render(metadata, results, seedComparisons);

await ShadowBenchmarkArtifactWriter.WriteV3bAsync(outputPath, markdown);
Console.WriteLine(outputPath);

static Dictionary<string, string> ParseArguments(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Arguments must be '--name value' pairs.");
        }

        parsed[values[index][2..]] = values[index + 1];
    }

    return parsed;
}

static string Required(IReadOnlyDictionary<string, string> arguments, string name) =>
    arguments.TryGetValue(name, out var value)
        ? value
        : throw new ArgumentException($"Missing required --{name} argument.");

static int ParsePositive(IReadOnlyDictionary<string, string> arguments, string name, int defaultValue)
{
    var value = arguments.TryGetValue(name, out var text) ? int.Parse(text) : defaultValue;
    return value > 0 ? value : throw new ArgumentOutOfRangeException(name, "Value must be positive.");
}

static int ParseNonNegative(IReadOnlyDictionary<string, string> arguments, string name, int defaultValue)
{
    var value = arguments.TryGetValue(name, out var text) ? int.Parse(text) : defaultValue;
    return value >= 0 ? value : throw new ArgumentOutOfRangeException(name, "Value must be non-negative.");
}