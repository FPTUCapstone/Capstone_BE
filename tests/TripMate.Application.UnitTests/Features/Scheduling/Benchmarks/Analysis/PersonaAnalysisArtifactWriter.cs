using System.Text;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public static class PersonaAnalysisArtifactWriter
{
    public static async Task<string> WriteAsync(
        string outputPath,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        var fullPath = PersonaAnalysisOutputPath.Validate(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var trimmed = markdown.TrimEnd('\r', '\n');
        await File.WriteAllTextAsync(
            fullPath,
            trimmed,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        return fullPath;
    }
}