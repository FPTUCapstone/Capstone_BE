using Microsoft.Data.SqlClient;

namespace TripMate.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Where the per-process template backup lives on the SQL Server host and the logical file names
/// a clone restore must move. All paths are server paths, never paths on the test host.
/// </summary>
internal sealed record SqlServerTemplateHandle(
    string BackupPath,
    string DataLogicalName,
    string LogLogicalName,
    string DataDirectory,
    string LogDirectory);

/// <summary>
/// Builds the current schema once per test process into a throwaway template database, backs it up
/// to the server's default backup folder and drops it. Concurrent callers share one build; a failed
/// build stays cached so every caller fails fast with the same exception.
/// </summary>
internal sealed class SqlServerTemplateDatabase
{
    private static readonly Lazy<SqlServerTemplateDatabase> SharedInstance = new(
        CreateShared,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string _configuredConnectionString;
    private readonly string _schemaPath;
    private readonly string _migrationsDirectory;
    private readonly Lazy<Task<SqlServerTemplateHandle>> _handle;
    private int _buildCount;
    private string? _templateName;

    internal SqlServerTemplateDatabase(
        string configuredConnectionString,
        string schemaPath,
        string migrationsDirectory)
    {
        _configuredConnectionString = configuredConnectionString;
        _schemaPath = schemaPath;
        _migrationsDirectory = migrationsDirectory;
        _handle = new Lazy<Task<SqlServerTemplateHandle>>(
            () =>
            {
                Interlocked.Increment(ref _buildCount);
                return BuildAsync();
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Never attach a probe or fault injector around the first use of Shared; use your own instance.</summary>
    internal static SqlServerTemplateDatabase Shared => SharedInstance.Value;

    internal int BuildCount => Volatile.Read(ref _buildCount);

    internal string? TemplateName => Volatile.Read(ref _templateName);

    internal Task<SqlServerTemplateHandle> GetHandleAsync() => _handle.Value;

    /// <summary>
    /// Restores the template backup into a new test database on one master connection, building
    /// the template first if no caller has yet. No retry: a failed restore surfaces to the caller,
    /// which owns dropping the partial database.
    /// </summary>
    internal async Task RestoreCloneAsync(string cloneName)
    {
        if (!SqlServerManagedNames.IsTestName(cloneName))
        {
            throw new InvalidOperationException(
                "Refusing to manage a database outside the isolated test naming convention.");
        }

        var handle = await GetHandleAsync();
        var dataPath = SqlServerServerPath.Combine(handle.DataDirectory, cloneName + ".mdf");
        var logPath = SqlServerServerPath.Combine(handle.LogDirectory, cloneName + "_log.ldf");

        await using var master = new SqlConnection(ConnectionStringFor("master"));
        await SqlServerTestDatabase.OpenConnectionAsync(master);
        await ExecuteAsync(
            master,
            $"""
            RESTORE DATABASE [{cloneName}] FROM DISK = N'{EscapeLiteral(handle.BackupPath)}'
            WITH MOVE N'{EscapeLiteral(handle.DataLogicalName)}' TO N'{EscapeLiteral(dataPath)}',
                 MOVE N'{EscapeLiteral(handle.LogLogicalName)}' TO N'{EscapeLiteral(logPath)}';
            """);
    }

    internal static (string Data, string Log) PickLogicalNames(
        IReadOnlyList<(string Name, string TypeDesc)> files)
    {
        var dataFiles = files.Where(file => file.TypeDesc == "ROWS").ToList();
        var logFiles = files.Where(file => file.TypeDesc == "LOG").ToList();

        if (dataFiles.Count != 1 || logFiles.Count != 1 || files.Count != 2)
        {
            throw new InvalidOperationException(
                "The template database must have exactly one ROWS and one LOG file; found "
                + $"{dataFiles.Count} ROWS and {logFiles.Count} LOG among {files.Count} files.");
        }

        return (dataFiles[0].Name, logFiles[0].Name);
    }

    private static SqlServerTemplateDatabase CreateShared()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable(
            SqlServerTestDatabase.ConnectionStringEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            throw new InvalidOperationException(
                $"{SqlServerTestDatabase.ConnectionStringEnvironmentVariable} was removed after test discovery.");
        }

        return new SqlServerTemplateDatabase(
            configuredConnectionString,
            SqlServerTestDatabase.DefaultSchemaPath(),
            SqlServerTestDatabase.DefaultMigrationsDirectory());
    }

    private async Task<SqlServerTemplateHandle> BuildAsync()
    {
        var templateName = SqlServerManagedNames.NewTemplateName();
        Volatile.Write(ref _templateName, templateName);

        var masterConnectionString = ConnectionStringFor("master");
        var templateConnectionString = ConnectionStringFor(templateName);

        var templateMayExist = false;
        try
        {
            SqlServerTemplateHandle handle;

            // One master connection for every master-level step (CI-787); the schema and
            // migrations use one template connection; the guarded drop opens its own.
            await using (var master = new SqlConnection(masterConnectionString))
            {
                await SqlServerTestDatabase.OpenConnectionAsync(master);
                var dataDirectory = await ReadServerPathAsync(master, "InstanceDefaultDataPath");
                var logDirectory = await ReadServerPathAsync(master, "InstanceDefaultLogPath");
                var backupDirectory = await ReadServerPathAsync(master, "InstanceDefaultBackupPath");

                templateMayExist = true;
                await ExecuteAsync(
                    master,
                    $"CREATE DATABASE [{templateName}] COLLATE Vietnamese_100_CI_AS;");

                await SqlServerTestDatabase.ApplyCurrentSchemaAsync(
                    templateConnectionString,
                    _schemaPath,
                    _migrationsDirectory);

                var (dataLogicalName, logLogicalName) = PickLogicalNames(
                    await ReadDatabaseFilesAsync(master, templateName));

                var backupPath = SqlServerServerPath.Combine(backupDirectory, templateName + ".bak");
                await ExecuteAsync(master, $"ALTER DATABASE [{templateName}] SET RECOVERY SIMPLE;");
                await ExecuteAsync(
                    master,
                    $"BACKUP DATABASE [{templateName}] TO DISK = N'{EscapeLiteral(backupPath)}' WITH INIT;");

                handle = new SqlServerTemplateHandle(
                    backupPath,
                    dataLogicalName,
                    logLogicalName,
                    dataDirectory,
                    logDirectory);
            }

            await SqlServerTestDatabase.DropDatabaseAsync(masterConnectionString, templateName);
            return handle;
        }
        catch (Exception) when (templateMayExist)
        {
            try
            {
                await SqlServerTestDatabase.DropDatabaseAsync(masterConnectionString, templateName);
            }
            catch (Exception)
            {
                // Best effort only: the build failure is the error every caller must see.
            }

            throw;
        }
    }

    private string ConnectionStringFor(string initialCatalog) =>
        new SqlConnectionStringBuilder(_configuredConnectionString)
        {
            InitialCatalog = initialCatalog,
            Pooling = false,
        }.ConnectionString;

    private static async Task<string> ReadServerPathAsync(SqlConnection master, string propertyName)
    {
        await using var command = master.CreateCommand();
        command.CommandText = "SELECT CONVERT(NVARCHAR(4000), SERVERPROPERTY(@property));";
        command.CommandTimeout = 120;
        command.Parameters.AddWithValue("@property", propertyName);

        if (await command.ExecuteScalarAsync() is not string path || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"SQL Server did not report SERVERPROPERTY('{propertyName}'); "
                + "the SQL test template database cannot be built without it.");
        }

        return path;
    }

    private static async Task<IReadOnlyList<(string Name, string TypeDesc)>> ReadDatabaseFilesAsync(
        SqlConnection master,
        string databaseName)
    {
        await using var command = master.CreateCommand();
        command.CommandText = """
            SELECT name, type_desc
            FROM sys.master_files
            WHERE database_id = DB_ID(@name);
            """;
        command.CommandTimeout = 120;
        command.Parameters.AddWithValue("@name", databaseName);

        var files = new List<(string Name, string TypeDesc)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            files.Add((reader.GetString(0), reader.GetString(1)));
        }

        return files;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static string EscapeLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}