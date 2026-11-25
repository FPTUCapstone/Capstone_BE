using FluentAssertions;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public class PersonaAnalysisArtifactWriterTests
{
    [Theory]
    [InlineData("docs/benchmarks/UC-10-persona-analysis-2026-10-21.md", true)]
    [InlineData("docs/benchmarks/UC-10-persona-analysis-v3b.md", true)]
    [InlineData("docs/benchmarks/shadow-local-2026-10-21-v3b.md", false)]
    [InlineData("docs/benchmarks/arbitrary-name.txt", false)]
    [InlineData("", false)]
    public void Validate_EnforcesStandardArtifactNaming(string path, bool expectedValid)
    {
        var action = () => PersonaAnalysisOutputPath.Validate(path);

        if (expectedValid)
        {
            action.Should().NotThrow();
            PersonaAnalysisOutputPath.Validate(path).Should().EndWith(".md");
        }
        else
        {
            action.Should().Throw<ArgumentException>();
        }
    }
}