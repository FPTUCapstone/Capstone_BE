namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public static class ShadowBenchmarkOutputPath
{
    public static string ValidateV3b(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetFileName(fullPath).EndsWith("-v3b.md", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Shadow benchmark output must use a new '*-v3b.md' file name.", nameof(path));
        }

        return fullPath;
    }
}