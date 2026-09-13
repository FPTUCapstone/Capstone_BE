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

internal sealed class SqlServerTestDatabase : IAsyncDisposable
{
    public const string ConnectionStringEnvironmentVariable =
        "TRIPMATE_SQLSERVER_TEST_CONNECTION";

    private const string DatabaseNamePrefix = "TripMate_Test_";
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
        var configuredConnectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringEnvironmentVariable} was removed after test discovery.");
        }

        var databaseName = $"{DatabaseNamePrefix}{Guid.NewGuid():N}";
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
            await database.InitializeAsync();
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

        if (interceptors.Length > 0)
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
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        await connection.OpenAsync(cancellationToken);
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

        EnsureSafeDatabaseName(DatabaseName);

        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END;
            """;
        await command.ExecuteNonQueryAsync();
        _databaseCreated = false;
    }

    private async Task InitializeAsync()
    {
        EnsureSafeDatabaseName(DatabaseName);

        await using (var connection = new SqlConnection(_masterConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"CREATE DATABASE [{DatabaseName}] COLLATE Vietnamese_100_CI_AS;";
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
            _databaseCreated = true;
        }

        var schemaPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "tripmate_schema_v7.sql");
        var schema = await File.ReadAllTextAsync(schemaPath);

        await using var schemaConnection = new SqlConnection(ConnectionString);
        await schemaConnection.OpenAsync();

        await using (var quotedIdentifierCommand = schemaConnection.CreateCommand())
        {
            quotedIdentifierCommand.CommandText = "SET QUOTED_IDENTIFIER ON;";
            await quotedIdentifierCommand.ExecuteNonQueryAsync();
        }

        foreach (var batch in BatchSeparator.Split(schema))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = schemaConnection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static void EnsureSafeDatabaseName(string databaseName)
    {
        if (!databaseName.StartsWith(DatabaseNamePrefix, StringComparison.Ordinal)
            || databaseName.Length != DatabaseNamePrefix.Length + 32
            || databaseName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new InvalidOperationException(
                "Refusing to manage a database outside the isolated test naming convention.");
        }
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