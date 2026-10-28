using System.Text;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public static class ShadowBenchmarkArtifactWriter
{
    public static async Task<string> WriteV3bAsync(
        string outputPath,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ShadowBenchmarkOutputPath.ValidateV3b(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            markdown,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        return fullPath;
    }
}