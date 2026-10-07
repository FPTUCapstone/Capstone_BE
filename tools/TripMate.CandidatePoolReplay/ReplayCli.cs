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
            string inputPath = Required(options, "--input");
            string manifestPath = Required(options, "--manifest");
            string outputPath = Required(options, "--output");
            options.TryGetValue("--timing-output", out string? timingOutputPath);
            temporaryPath = outputPath + ".tmp";
            string corpus = await File.ReadAllTextAsync(inputPath);
            string manifestJson = await File.ReadAllTextAsync(manifestPath);
            ReplayManifest manifest = JsonSerializer.Deserialize(
                manifestJson,
                ReplayJsonContext.Default.ReplayManifest)
                ?? throw new ReplayValidationException("Manifest is null.");
            ReplayOutput output;
            ReplayTimingReport? timing = null;
            if (string.IsNullOrWhiteSpace(timingOutputPath))
            {
                output = ReplayEngine.Run(corpus, manifest);
            }
            else
            {
                ReplayMeasuredOutput measured = ReplayEngine.RunMeasured(corpus, manifest);
                output = measured.Output;
                timing = measured.Timing;
                temporaryTimingPath = timingOutputPath + ".tmp";
            }

            string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(directory!);
            await File.WriteAllTextAsync(temporaryPath, ReplayEngine.Serialize(output));
            if (timing is not null)
            {
                string? timingDirectory = Path.GetDirectoryName(Path.GetFullPath(timingOutputPath!));
                Directory.CreateDirectory(timingDirectory!);
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
}