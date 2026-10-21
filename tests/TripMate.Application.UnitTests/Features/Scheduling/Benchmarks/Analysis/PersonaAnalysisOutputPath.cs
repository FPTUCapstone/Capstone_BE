namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public static class PersonaAnalysisOutputPath
{
    public static string Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileName(fullPath);
        if (!fileName.StartsWith("UC-10-persona-analysis-", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Persona analysis output must use a file name starting with 'UC-10-persona-analysis-' and ending with '.md'.",
                nameof(path));
        }

        return fullPath;
    }
}