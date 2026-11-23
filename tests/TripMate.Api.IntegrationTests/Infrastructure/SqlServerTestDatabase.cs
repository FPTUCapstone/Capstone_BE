using System.Data;
using System.Text.RegularExpressions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Infrastructure;

internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            SqlServerTestDatabase.ConnectionStringEnvironmentVariable)))
        {
            Skip = $"Set {SqlServerTestDatabase.ConnectionStringEnvironmentVariable} "
                + "to run SQL Server integration tests.";
        }
    }
}

internal sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            SqlServerTestDatabase.ConnectionStringEnvironmentVariable)))
        {
            Skip = $"Set {SqlServerTestDatabase.ConnectionStringEnvironmentVariable} "
                + "to run SQL Server integration tests.";
        }
    }
}

internal sealed class SqlServerTestDatabase : IAsyncDisposable
{
    public const string ConnectionStringEnvironmentVariable =
        "TRIPMATE_SQLSERVER_TEST_CONNECTION";

    private static readonly Regex BatchSeparator = new(
        @"^\s*GO\s*(?:--.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly string _masterConnectionString;
    private bool _databaseCreated;

    private SqlServerTestDatabase(
        string masterConnectionString,
        string databaseName,
        string connectionString)
    {
        _masterConnectionString = masterConnectionString;
        DatabaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public static async Task<SqlServerTestDatabase> CreateAsync()
    {
        return await CreateAsync(applyCurrentSchema: true);
    }

    public static async Task<SqlServerTestDatabase> CreateEmptyAsync()
    {
        return await CreateAsync(applyCurrentSchema: false);
    }

    public async Task ExecuteScriptAsync(
        string scriptPath,
        CancellationToken cancellationToken = default)
    {
        var script = await File.ReadAllTextAsync(scriptPath, cancellationToken);
        await ExecuteBatchesAsync(script, cancellationToken);
    }

    private static async Task<SqlServerTestDatabase> CreateAsync(bool applyCurrentSchema)
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringEnvironmentVariable} was removed after test discovery.");
        }

        var databaseName = SqlServerManagedNames.NewTestName();
        var masterConnection = new SqlConnectionStringBuilder(configuredConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        };
        var databaseConnection = new SqlConnectionStringBuilder(configuredConnectionString)
        {
            InitialCatalog = databaseName,
            Pooling = false,
        };

        var database = new SqlServerTestDatabase(
            masterConnection.ConnectionString,
            databaseName,
            databaseConnection.ConnectionString);

        try
        {
            await database.InitializeAsync(applyCurrentSchema);
            return database;
        }
        catch (Exception initializationException)
        {
            try
            {
                await database.DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "SQL Server test database initialization and cleanup both failed.",
                    initializationException,
                    cleanupException);
            }

            throw;
        }
    }

    public ApplicationDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(ConnectionString);

        if (interceptors is { Length: > 0 })
        {
            builder.AddInterceptors(interceptors);
        }

        return new ApplicationDbContext(builder.Options);
    }

    public async Task<ExplorationDatabaseCounts> ReadExplorationCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = CreateDbContext();

        return new ExplorationDatabaseCounts(
            await context.Users.CountAsync(cancellationToken),
            await context.PoiCategories.CountAsync(cancellationToken),
            await context.Tags.CountAsync(cancellationToken),
            await context.PointsOfInterest.CountAsync(cancellationToken),
            await context.PoiOpeningHours.CountAsync(cancellationToken),
            await context.PoiTags.CountAsync(cancellationToken),
            await context.PoiPhotos.CountAsync(cancellationToken),
            await context.Reviews.CountAsync(cancellationToken),
            await context.AuditLogs.CountAsync(cancellationToken));
    }

    public async Task ExecuteNonQueryAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<T> ExecuteScalarAsync<T>(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 120;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return (T)Convert.ChangeType(value, typeof(T));
    }

    public async Task<long?> ReadPoiIdentityLastValueAsync(
        CancellationToken cancellationToken = default)
    {
        const string commandText = """
            SELECT CONVERT(BIGINT, last_value)
            FROM sys.identity_columns
            WHERE object_id = OBJECT_ID(N'catalog.POIs');
            """;

        await using var connection = new SqlConnection(ConnectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_databaseCreated)
        {
            return;
        }

        // Workspace policy may require keeping isolated test databases for review.
        if (Environment.GetEnvironmentVariable("TRIPMATE_SQLSERVER_TEST_PRESERVE") == "1")
        {
            return;
        }

        await DropDatabaseAsync(_masterConnectionString, DatabaseName);
        _databaseCreated = false;
    }

    internal static string DefaultSchemaPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Database", "tripmate_schema_v7.sql");
    }

    internal static string DefaultMigrationsDirectory()
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "database",
            "migrations");
    }

    internal static async Task ApplyCurrentSchemaAsync(
        string databaseConnectionString,
        string schemaPath,
        string migrationsDirectory)
    {
        await using var schemaConnection = new SqlConnection(databaseConnectionString);
        await OpenConnectionAsync(schemaConnection);
        await ExecuteBatchesAsync(schemaConnection, await File.ReadAllTextAsync(schemaPath));

        foreach (var migrationPath in Directory
            .EnumerateFiles(migrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            await ExecuteBatchesAsync(schemaConnection, await File.ReadAllTextAsync(migrationPath));
        }
    }

    internal static async Task DropDatabaseAsync(string masterConnectionString, string databaseName)
    {
        if (!SqlServerManagedNames.IsTestName(databaseName)
            && !SqlServerManagedNames.IsTemplateName(databaseName))
        {
            throw new InvalidOperationException(
                "Refusing to manage a database outside the isolated test naming convention.");
        }

        await using var connection = new SqlConnection(masterConnectionString);
        await OpenConnectionAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END;
            """;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await command.ExecuteNonQueryAsync();
                return;
            }
            catch (SqlException exception) when (exception.Number == 1205 && attempt < 2)
            {
                // Deadlock rolls back the DDL statement; retry the guarded, idempotent cleanup batch.
                await Task.Delay(attempt == 0 ? 200 : 500);
            }
        }
    }

    private async Task InitializeAsync(bool applyCurrentSchema)
    {
        if (!SqlServerManagedNames.IsTestName(DatabaseName))
        {
            throw new InvalidOperationException(
                "Refusing to manage a database outside the isolated test naming convention.");
        }

        await using (var connection = new SqlConnection(_masterConnectionString))
        {
            await OpenConnectionAsync(connection);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"CREATE DATABASE [{DatabaseName}] COLLATE Vietnamese_100_CI_AS;";
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
            _databaseCreated = true;
        }

        if (!applyCurrentSchema)
        {
            return;
        }

        await ApplyCurrentSchemaAsync(
            ConnectionString,
            DefaultSchemaPath(),
            DefaultMigrationsDirectory());
    }

    private async Task ExecuteBatchesAsync(
        string script,
        CancellationToken cancellationToken = default)
    {
        await using var schemaConnection = new SqlConnection(ConnectionString);
        await OpenConnectionAsync(schemaConnection, cancellationToken);
        await ExecuteBatchesAsync(schemaConnection, script, cancellationToken);
    }

    private static async Task ExecuteBatchesAsync(
        SqlConnection schemaConnection,
        string script,
        CancellationToken cancellationToken = default)
    {
        await using (var quotedIdentifierCommand = schemaConnection.CreateCommand())
        {
            quotedIdentifierCommand.CommandText = "SET QUOTED_IDENTIFIER ON;";
            await quotedIdentifierCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var batch in BatchSeparator.Split(script))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = schemaConnection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static async Task OpenConnectionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await connection.OpenAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (
                connection.State == ConnectionState.Closed
                && (exception is TimeoutException || exception is SqlException { Number: -2 }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt == 2) throw;
                // Only opening is retried. No SQL command has executed on this connection.
                await Task.Delay(attempt == 0 ? 200 : 500, cancellationToken);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the TripMate repository root.");
    }
}

internal sealed record ExplorationDatabaseCounts(
    int Users,
    int Categories,
    int Tags,
    int Pois,
    int OpeningHours,
    int PoiTags,
    int Photos,
    int Reviews,
    int AuditLogs);