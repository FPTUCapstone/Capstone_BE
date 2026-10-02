using FluentAssertions;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class DatabaseScriptSafetyTests
{
    [Fact]
    public async Task SeedImage_AppliesSchemaMigrationsAfterBaseSchema()
    {
        var dockerfile = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Database", "Dockerfile.seeded"));
        var seedScript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Database", "seed-image.sh"));

        dockerfile.Should().Contain("COPY database/migrations /tmp/migrations");
        seedScript.Should().Contain("for migrationPath in /tmp/migrations/*.sql");
        seedScript.Should().Contain("-i \"$migrationPath\"");
    }

    [Fact]
    public async Task ItineraryVersionOperationsMigration_IsIdempotentAndContainsRequiredGuards()
    {
        var migration = await File.ReadAllTextAsync(
            Path.Combine(
                AppContext.BaseDirectory,
                "Database",
                "migrations",
                "20260920_add_itinerary_version_operations.sql"));

        migration.Should().Contain("IF OBJECT_ID(N'planning.ItineraryVersionOperations', N'U') IS NULL");
        migration.Should().Contain("CK_ItineraryVersionOperations_Type");
        migration.Should().Contain("UQ_ItineraryVersionOperations_TravelerKey");
        migration.Should().Contain("FK_ItineraryVersionOperations_Source");
        migration.Should().Contain("FK_ItineraryVersionOperations_Result");
        migration.Should().Contain("COMMIT TRANSACTION");
    }

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