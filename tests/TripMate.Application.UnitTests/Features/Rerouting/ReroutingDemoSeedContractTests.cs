using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using FluentAssertions;

using TripMate.Application.Features.Rerouting.Common;

namespace TripMate.Application.UnitTests.Features.Rerouting;

/// <summary>
/// The UAT demo seed builds the proposal snapshot in T-SQL. It cannot be executed by a test (seeds are verified on Azure,
/// never locally), so this guard only checks, offline, that the seed still writes every field of snapshot schema v1.
/// </summary>
public sealed class ReroutingDemoSeedContractTests
{
    // Nullable and omitted on purpose: the engine passes no per-leg transport override.
    private static readonly string[] OptionalFields = ["transportModeToNext"];

    [Fact]
    public void TheDemoSeedWritesEveryFieldOfSnapshotSchemaV1()
    {
        var sql = File.ReadAllText(SeedPath());
        var fields = new[]
            {
                typeof(ReroutingProposalSnapshot),
                typeof(ReroutingAnchorSnapshot),
                typeof(ReroutingBaseStop),
                typeof(ReroutingProposedStop),
            }
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(property => property.Name != nameof(ReroutingProposalSnapshot.CurrentSchemaVersion))
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Distinct()
            .Except(OptionalFields)
            .ToArray();

        foreach (var field in fields)
        {
            var written = sql.Contains($"\"{field}\"", StringComparison.Ordinal)
                || Regex.IsMatch(sql, $@"\bAS {field}\b");
            written.Should().BeTrue($"the demo seed must write the snapshot field '{field}'");
        }
    }

    [Fact]
    public void TheDemoSeedIsADryRunByDefaultAndNeverRunsWithoutASession()
    {
        var sql = File.ReadAllText(SeedPath());

        sql.Should().Contain("DECLARE @DryRun BIT = 1;");
        sql.Should().Contain("DECLARE @SessionId BIGINT = NULL;");
        sql.Should().Contain("THROW 51500");
        sql.Should().Contain("ROLLBACK TRANSACTION;");
    }

    private static string SeedPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "database", "seeds", "20261125_seed_uc15_demo_rerouting_proposal.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("The UC-15 demo seed was not found above the test output folder.");
    }
}