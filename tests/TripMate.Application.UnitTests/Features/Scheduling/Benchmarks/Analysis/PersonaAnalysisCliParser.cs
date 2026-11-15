namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public sealed record PersonaAnalysisCliOptions(
    string Mode,
    string OutputPath,
    string? FixtureOutputPath);

public static class PersonaAnalysisCliParser
{
    public static PersonaAnalysisCliOptions Parse(string[] args)
    {
        var arguments = ParseArguments(args);
        var outputPath = PersonaAnalysisOutputPath.Validate(Required(arguments, "output"));
        var mode = arguments.TryGetValue("mode", out var m) ? m.ToLowerInvariant() : "recorded";
        if (mode != "live" && mode != "recorded")
        {
            throw new ArgumentException($"Invalid --mode '{mode}'. Supported modes are 'live' and 'recorded'.");
        }
        var fixtureOutput = arguments.TryGetValue("fixture-output", out var fo) ? fo : null;
        if (mode == "recorded" && !string.IsNullOrWhiteSpace(fixtureOutput))
        {
            throw new ArgumentException("--fixture-output is only supported in live mode.");
        }

        return new PersonaAnalysisCliOptions(mode, outputPath, fixtureOutput);
    }

    private static Dictionary<string, string> ParseArguments(string[] values)
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

    private static string Required(IReadOnlyDictionary<string, string> arguments, string name) =>
        arguments.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"Missing required --{name} argument.");
}