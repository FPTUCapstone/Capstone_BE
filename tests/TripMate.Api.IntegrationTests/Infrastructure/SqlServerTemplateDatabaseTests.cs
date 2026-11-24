using FluentAssertions;

using Microsoft.Data.SqlClient;

using Xunit.Abstractions;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class SqlServerTemplateDatabaseTests(ITestOutputHelper output)
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task GetHandleAsync_ConcurrentCallers_BuildTheTemplateOnce()
    {
        var template = NewTemplate();

        var handles = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => template.GetHandleAsync())));

        template.BuildCount.Should().Be(1);
        handles.Should().AllBeEquivalentTo(handles[0]);
        handles.Should().OnlyContain(handle => ReferenceEquals(handle, handles[0]));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task GetHandleAsync_AfterBuild_LeavesNoTemplateDatabase()
    {
        var template = NewTemplate();

        var handle = await template.GetHandleAsync();

        template.TemplateName.Should().NotBeNull();
        (await DatabaseExistsAsync(template.TemplateName!)).Should().BeFalse(
            "the template database is dropped right after the backup");
        handle.BackupPath.Should().NotBeNullOrWhiteSpace();
        handle.DataLogicalName.Should().NotBeNullOrWhiteSpace();
        handle.LogLogicalName.Should().NotBeNullOrWhiteSpace();
        handle.DataDirectory.Should().NotBeNullOrWhiteSpace();
        handle.LogDirectory.Should().NotBeNullOrWhiteSpace();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task GetHandleAsync_BrokenMigration_FailsEveryCallerAndLeavesNothingBehind()
    {
        var migrationsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"tripmate-template-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(migrationsDirectory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(migrationsDirectory, "0001_broken.sql"),
                "THROW 51001, 'broken', 1;");
            var template = new SqlServerTemplateDatabase(
                ConfiguredConnectionString(),
                SqlServerTestDatabase.DefaultSchemaPath(),
                migrationsDirectory);

            var failures = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() => Record.ExceptionAsync(() => template.GetHandleAsync()))));

            foreach (var failure in failures)
            {
                failure.Should().BeOfType<SqlException>().Which.Number.Should().Be(51001);
            }

            template.BuildCount.Should().Be(1);
            template.TemplateName.Should().NotBeNull();
            (await DatabaseExistsAsync(template.TemplateName!)).Should().BeFalse(
                "a failed build drops its partial template database");

            var later = await Record.ExceptionAsync(() => template.GetHandleAsync());

            later.Should().BeSameAs(failures[0], "a faulted build stays cached");
            template.BuildCount.Should().Be(1, "a failed build is not repeated");
        }
        finally
        {
            Directory.Delete(migrationsDirectory, recursive: true);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task GetHandleAsync_ReportsServerPaths()
    {
        var template = NewTemplate();

        var handle = await template.GetHandleAsync();

        var backupDirectory = await ReadServerPropertyAsync("InstanceDefaultBackupPath");
        var trimmedBackupDirectory = backupDirectory.EndsWith('\\') || backupDirectory.EndsWith('/')
            ? backupDirectory[..^1]
            : backupDirectory;
        var backupSize = await ReadBackupSizeAsync(template.TemplateName!);
        output.WriteLine($"BackupPath: {handle.BackupPath}");
        output.WriteLine($"DataDirectory: {handle.DataDirectory}");
        output.WriteLine($"LogDirectory: {handle.LogDirectory}");
        output.WriteLine($"Backup size (bytes): {backupSize}");

        handle.BackupPath.Should().EndWith(".bak");
        handle.BackupPath.Should().StartWith(trimmedBackupDirectory);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task BuildAsync_UsesAtMostThreeConnectionsEvenWhenOpeningIsRetried()
    {
        using var probe = new ConnectionProbe(openFailures: 1);
        var template = NewTemplate();

        await template.GetHandleAsync();
        var openAttempts = probe.OpenAttempts;
        var buildConnections = probe.DistinctConnections;
        probe.Dispose();
        output.WriteLine($"Build connections: {buildConnections}, open attempts: {openAttempts}");

        openAttempts.Should().BeGreaterThan(buildConnections, "the injected opening timeout must be retried");
        buildConnections.Should().BeLessThanOrEqualTo(3,
            "CI-787: schema and migrations share one connection (master, template, and the guarded drop)");
    }

    [Fact]
    public void PickLogicalNames_OneDataAndOneLog_ReturnsThem()
    {
        var files = new List<(string Name, string TypeDesc)>
        {
            ("TripMate_Template_log", "LOG"),
            ("TripMate_Template", "ROWS"),
        };

        var (data, log) = SqlServerTemplateDatabase.PickLogicalNames(files);

        data.Should().Be("TripMate_Template");
        log.Should().Be("TripMate_Template_log");
    }

    [Fact]
    public void PickLogicalNames_TwoDataFiles_Throws()
    {
        var files = new List<(string Name, string TypeDesc)>
        {
            ("TripMate_Template", "ROWS"),
            ("TripMate_Template_2", "ROWS"),
            ("TripMate_Template_log", "LOG"),
        };

        var act = () => SqlServerTemplateDatabase.PickLogicalNames(files);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PickLogicalNames_NoLogFile_Throws()
    {
        var files = new List<(string Name, string TypeDesc)>
        {
            ("TripMate_Template", "ROWS"),
        };

        var act = () => SqlServerTemplateDatabase.PickLogicalNames(files);

        act.Should().Throw<InvalidOperationException>();
    }

    private static SqlServerTemplateDatabase NewTemplate() => new(
        ConfiguredConnectionString(),
        SqlServerTestDatabase.DefaultSchemaPath(),
        SqlServerTestDatabase.DefaultMigrationsDirectory());

    private static string ConfiguredConnectionString() =>
        Environment.GetEnvironmentVariable(SqlServerTestDatabase.ConnectionStringEnvironmentVariable)
        ?? throw new InvalidOperationException(
            $"{SqlServerTestDatabase.ConnectionStringEnvironmentVariable} was removed after test discovery.");

    private static async Task<SqlConnection> OpenMasterAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConfiguredConnectionString())
        {
            InitialCatalog = "master",
            Pooling = false,
        };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> DatabaseExistsAsync(string databaseName)
    {
        await using var connection = await OpenMasterAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.AddWithValue("@name", databaseName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<string> ReadServerPropertyAsync(string propertyName)
    {
        await using var connection = await OpenMasterAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(NVARCHAR(4000), SERVERPROPERTY(@property));";
        command.Parameters.AddWithValue("@property", propertyName);
        var value = await command.ExecuteScalarAsync() as string;
        value.Should().NotBeNullOrWhiteSpace($"the server must report {propertyName}");
        return value!;
    }

    private static async Task<decimal?> ReadBackupSizeAsync(string databaseName)
    {
        await using var connection = await OpenMasterAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) backup_size
            FROM msdb.dbo.backupset
            WHERE database_name = @name
            ORDER BY backup_finish_date DESC;
            """;
        command.Parameters.AddWithValue("@name", databaseName);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToDecimal(value);
    }
}