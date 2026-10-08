using System.Text.Json;

namespace TripMate.CandidatePoolReplay;

public static class ReplayCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter error)
    {
        string? temporaryPath = null;
        string? temporaryTimingPath = null;
        try
        {
            Dictionary<string, string> options = Parse(args);
            string inputPath = NormalizePath(Required(options, "--input"));
            string manifestPath = NormalizePath(Required(options, "--manifest"));
            string outputPath = NormalizePath(Required(options, "--output"));
            options.TryGetValue("--timing-output", out string? timingOutputPath);
            timingOutputPath = string.IsNullOrWhiteSpace(timingOutputPath)
                ? null
                : NormalizePath(timingOutputPath);
            ValidateDistinctPaths(inputPath, manifestPath, outputPath, timingOutputPath);

            string corpus = await File.ReadAllTextAsync(inputPath);
            string manifestJson = await File.ReadAllTextAsync(manifestPath);
            ReplayManifest manifest = JsonSerializer.Deserialize(
                manifestJson,
                ReplayJsonContext.Default.ReplayManifest)
                ?? throw new ReplayValidationException("Manifest is null.");
            ReplayOutput output;
            ReplayTimingReport? timing = null;
            if (timingOutputPath is null)
            {
                output = ReplayEngine.Run(corpus, manifest);
            }
            else
            {
                ReplayMeasuredOutput measured = ReplayEngine.RunMeasured(corpus, manifest);
                output = measured.Output;
                timing = measured.Timing;
            }

            string? directory = Path.GetDirectoryName(outputPath);
            Directory.CreateDirectory(directory!);
            temporaryPath = TemporaryPath(outputPath);
            await File.WriteAllTextAsync(temporaryPath, ReplayEngine.Serialize(output));
            if (timing is not null)
            {
                string? timingDirectory = Path.GetDirectoryName(timingOutputPath!);
                Directory.CreateDirectory(timingDirectory!);
                temporaryTimingPath = TemporaryPath(timingOutputPath!);
                await File.WriteAllTextAsync(
                    temporaryTimingPath!,
                    ReplayEngine.SerializeTiming(timing));
                File.Move(temporaryTimingPath!, timingOutputPath!, overwrite: true);
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or JsonException
            or ReplayValidationException)
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (temporaryTimingPath is not null && File.Exists(temporaryTimingPath))
            {
                File.Delete(temporaryTimingPath);
            }

            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        if (args.Length % 2 != 0)
        {
            throw new ArgumentException("Arguments must be provided as --name value pairs.");
        }

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!parsed.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException($"Duplicate argument '{args[index]}'.");
            }
        }

        return parsed;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument '{name}'.");

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static void ValidateDistinctPaths(
        string inputPath,
        string manifestPath,
        string outputPath,
        string? timingOutputPath)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (comparer.Equals(outputPath, inputPath)
            || comparer.Equals(outputPath, manifestPath)
            || timingOutputPath is not null
            && (comparer.Equals(timingOutputPath, inputPath)
                || comparer.Equals(timingOutputPath, manifestPath)
                || comparer.Equals(timingOutputPath, outputPath)))
        {
            throw new ArgumentException(
                "Input, manifest, output, and timing output paths must not refer to the same file.");
        }
    }

    private static string TemporaryPath(string destinationPath)
    {
        string directory = Path.GetDirectoryName(destinationPath)!;
        string fileName = Path.GetFileName(destinationPath);
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
    }
}