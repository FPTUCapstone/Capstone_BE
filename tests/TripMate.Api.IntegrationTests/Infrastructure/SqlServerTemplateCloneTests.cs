using FluentAssertions;

using Microsoft.Data.SqlClient;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class SqlServerTemplateCloneTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateAsync_Clone_MatchesAFreshlyBuiltDatabase()
    {
        await using var clone = await SqlServerTestDatabase.CreateAsync();
        SqlServerTemplateDatabase.Shared.BuildCount.Should().Be(1,
            "CreateAsync() restores a clone of the process-wide template");
        await using var fresh = await SqlServerTestDatabase.CreateEmptyAsync();
        await SqlServerTestDatabase.ApplyCurrentSchemaAsync(
            fresh.ConnectionString,
            SqlServerTestDatabase.DefaultSchemaPath(),
            SqlServerTestDatabase.DefaultMigrationsDirectory());

        var cloneObjects = await ReadObjectsAsync(clone);
        var freshObjects = await ReadObjectsAsync(fresh);

        cloneObjects.Should().NotBeEmpty();
        cloneObjects.Should().Equal(freshObjects);
        (await ReadUserTableRowCountAsync(clone)).Should().Be(await ReadUserTableRowCountAsync(fresh));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateAsync_Clones_AreIsolatedAndDropIndependently()
    {
        var first = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await using var second = await SqlServerTestDatabase.CreateAsync();
            var tagCount = await second.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.Tags;");

            await first.ExecuteNonQueryAsync("INSERT INTO catalog.Tags (name) VALUES (N'clone-isolation-probe');");

            (await first.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM catalog.Tags WHERE name = N'clone-isolation-probe';")).Should().Be(1);
            (await second.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM catalog.Tags WHERE name = N'clone-isolation-probe';")).Should().Be(0);
            (await second.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.Tags;")).Should().Be(tagCount);

            await first.DisposeAsync();

            (await second.ExecuteScalarAsync<int>("SELECT 1;")).Should().Be(1);
            (await ReadDatabaseStateAsync(first.DatabaseName)).Should().BeNull(
                "disposing one clone drops only that clone");
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateAsync_FromBuiltTemplate_UsesOneConnectionAndRunsNoScript()
    {
        var template = new SqlServerTemplateDatabase(
            ConfiguredConnectionString(),
            SqlServerTestDatabase.DefaultSchemaPath(),
            SqlServerTestDatabase.DefaultMigrationsDirectory());
        await template.GetHandleAsync();

        using var probe = new ConnectionProbe();
        await using var database = await SqlServerTestDatabase.CreateAsync(template);
        var cloneConnections = probe.DistinctConnections;
        probe.Dispose();

        cloneConnections.Should().Be(1, "a clone is one RESTORE on one master connection");
        template.BuildCount.Should().Be(1, "a clone from a built template runs no schema script");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'AuditLogs';"))
            .Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DropDatabaseAsync_DatabaseLeftInRestoringState_StillDropsIt()
    {
        var handle = await SqlServerTemplateDatabase.Shared.GetHandleAsync();
        var name = SqlServerManagedNames.NewTestName();
        var masterConnectionString = MasterConnectionString();
        try
        {
            var dataPath = SqlServerServerPath.Combine(handle.DataDirectory, name + ".mdf");
            var logPath = SqlServerServerPath.Combine(handle.LogDirectory, name + "_log.ldf");
            await using (var connection = new SqlConnection(masterConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 120;
                command.CommandText = $"""
                    RESTORE DATABASE [{name}] FROM DISK = N'{Escape(handle.BackupPath)}'
                    WITH MOVE N'{Escape(handle.DataLogicalName)}' TO N'{Escape(dataPath)}',
                         MOVE N'{Escape(handle.LogLogicalName)}' TO N'{Escape(logPath)}',
                         NORECOVERY;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            (await ReadDatabaseStateAsync(name)).Should().Be("RESTORING");

            await SqlServerTestDatabase.DropDatabaseAsync(masterConnectionString, name);

            (await ReadDatabaseStateAsync(name)).Should().BeNull();
        }
        finally
        {
            await SqlServerTestDatabase.DropDatabaseAsync(masterConnectionString, name);
        }
    }

    private static async Task<List<string>> ReadObjectsAsync(SqlServerTestDatabase database)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // A system-named constraint ends in the hex of its object_id, which differs between any
        // two databases built by the same script, so it is compared by its table and column.
        command.CommandText = """
            WITH SystemNamed AS (
                SELECT object_id, 0 AS column_id FROM sys.key_constraints WHERE is_system_named = 1
                UNION ALL
                SELECT object_id, 0 FROM sys.foreign_keys WHERE is_system_named = 1
                UNION ALL
                SELECT object_id, parent_column_id FROM sys.default_constraints WHERE is_system_named = 1
                UNION ALL
                SELECT object_id, parent_column_id FROM sys.check_constraints WHERE is_system_named = 1)
            SELECT s.name COLLATE DATABASE_DEFAULT + N'.'
                + CASE
                    WHEN sn.object_id IS NULL THEN o.name COLLATE DATABASE_DEFAULT
                    ELSE N'(system-named on ' + OBJECT_NAME(o.parent_object_id) COLLATE DATABASE_DEFAULT
                        + COALESCE(N'.' + COL_NAME(o.parent_object_id, sn.column_id) COLLATE DATABASE_DEFAULT, N'')
                        + N')'
                  END
                + N':' + o.type COLLATE DATABASE_DEFAULT
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN SystemNamed AS sn ON sn.object_id = o.object_id
            WHERE o.is_ms_shipped = 0;
            """;

        var objects = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            objects.Add(reader.GetString(0));
        }

        objects.Sort(StringComparer.Ordinal);
        return objects;
    }

    private static Task<long> ReadUserTableRowCountAsync(SqlServerTestDatabase database) =>
        database.ExecuteScalarAsync<long>("""
            SELECT COALESCE(SUM(p.row_count), 0)
            FROM sys.dm_db_partition_stats AS p
            JOIN sys.objects AS o ON o.object_id = p.object_id
            WHERE o.type = 'U' AND o.is_ms_shipped = 0 AND p.index_id IN (0, 1);
            """);

    private static async Task<string?> ReadDatabaseStateAsync(string databaseName)
    {
        await using var connection = new SqlConnection(MasterConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_desc FROM sys.databases WHERE name = @name;";
        command.Parameters.AddWithValue("@name", databaseName);
        return await command.ExecuteScalarAsync() as string;
    }

    private static string MasterConnectionString() =>
        new SqlConnectionStringBuilder(ConfiguredConnectionString())
        {
            InitialCatalog = "master",
            Pooling = false,
        }.ConnectionString;

    private static string ConfiguredConnectionString() =>
        Environment.GetEnvironmentVariable(SqlServerTestDatabase.ConnectionStringEnvironmentVariable)
        ?? throw new InvalidOperationException(
            $"{SqlServerTestDatabase.ConnectionStringEnvironmentVariable} was removed after test discovery.");

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}