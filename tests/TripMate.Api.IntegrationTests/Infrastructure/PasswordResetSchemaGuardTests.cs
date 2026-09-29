using FluentAssertions;

using Xunit;

namespace TripMate.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Task 7 zero-schema guard: UC-06 must not have introduced any reset database artifact —
/// no PasswordResetCredentials table references, no reset columns/indexes/constraints in
/// the schema script, and no reset migration files. These are repository/file checks and
/// run without a database.
/// </summary>
public sealed class PasswordResetSchemaGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact(DisplayName = "PLAN-SEC-01: no source, test, database, or config file references PasswordResetCredentials")]
    public void NoFile_ReferencesPasswordResetCredentials()
    {
        var violations = new List<string>();
        foreach (var file in EnumerateRepoFiles())
        {
            // The guard's own source mentions the forbidden table name in its assertions.
            if (file.FullName.Contains("PasswordResetSchemaGuardTests", StringComparison.Ordinal))
            {
                continue;
            }

            var content = File.ReadAllText(file.FullName);
            if (content.Contains("PasswordResetCredentials", StringComparison.Ordinal))
            {
                violations.Add(file.FullName);
            }
        }

        violations.Should().BeEmpty("UC-06 requires ZERO reset database artifacts");
    }

    [Fact(DisplayName = "PLAN-SEC-02: no reset migration file exists under database/migrations")]
    public void NoResetMigrationFiles_Exist()
    {
        var migrationsDirectory = Path.Combine(RepoRoot, "database", "migrations");
        if (!Directory.Exists(migrationsDirectory))
        {
            return;
        }

        var resetMigrations = Directory.EnumerateFiles(migrationsDirectory)
            .Where(file => Path.GetFileName(file).Contains("reset", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).Contains("password", StringComparison.OrdinalIgnoreCase))
            .ToList();

        resetMigrations.Should().BeEmpty();
    }

    [Fact(DisplayName = "PLAN-SEC-03: the full schema script contains no UC-06 reset additions")]
    public void FullSchema_ContainsNoResetAdditions()
    {
        var schemaPath = Path.Combine(RepoRoot, "database", "tripmate_schema_v7.sql");
        var schema = File.ReadAllText(schemaPath);

        schema.Should().NotContain("password_reset");
        schema.Should().NotContain("PasswordReset");
        schema.Should().NotContain("reset_token");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Could not locate the repository root (TripMate.slnx).");
        }

        return directory.FullName;
    }

    private static IEnumerable<FileInfo> EnumerateRepoFiles()
    {
        foreach (var relativeRoot in new[] { "src", "tests", "database" })
        {
            var root = Path.Combine(RepoRoot, relativeRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var candidate = relative.Split(Path.DirectorySeparatorChar).First();
                if (candidate is "bin" or "obj" or "coverage")
                {
                    continue;
                }

                var extension = Path.GetExtension(file);
                if (extension is ".cs" or ".sql" or ".json" or ".csproj")
                {
                    yield return new FileInfo(file);
                }
            }
        }
    }
}