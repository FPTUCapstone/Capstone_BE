using FluentAssertions;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class DatabaseScriptSafetyTests
{
    [Theory]
    [InlineData("apply-schema.sh")]
    [InlineData("seed-image.sh")]
    public async Task SqlCmdInvocations_FailTheScriptForSqlErrors(string scriptFileName)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Database", scriptFileName);
        var script = await File.ReadAllTextAsync(scriptPath);

        var sqlCmdLines = script
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("\"$SQLCMD\"", StringComparison.Ordinal))
            .ToList();

        sqlCmdLines.Should().NotBeEmpty();
        sqlCmdLines.Should().OnlyContain(line =>
            line.Contains("-b", StringComparison.Ordinal)
            && line.Contains("-V 11", StringComparison.Ordinal));
    }
}
