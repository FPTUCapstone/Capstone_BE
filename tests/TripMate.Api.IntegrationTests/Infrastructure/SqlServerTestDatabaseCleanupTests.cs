using System.Diagnostics;

using FluentAssertions;

using Microsoft.Data.SqlClient;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class SqlServerTestDatabaseCleanupTests
{
    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DisposeAsync_TransientDeadlock_StillRemovesTheTemporaryDatabase(int failures)
    {
        RequireCleanupEnabled();
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await CreateProbeAsync(database);
        using var fault = new CleanupSqlFault(database, failures);
        try
        {
            var failure = await Record.ExceptionAsync(() => database.DisposeAsync().AsTask());

            if (failure is SqlException sqlException) sqlException.Number.Should().Be(1205);
            failure.Should().BeNull("cleanup must recover from a transient SQL 1205");
            fault.Attempts.Should().Be(failures + 1);
            (await DatabaseExistsAsync(database)).Should().BeFalse();
            await database.DisposeAsync();
            fault.Attempts.Should().Be(failures + 1, "disposal after successful cleanup is a no-op");
        }
        finally
        {
            try { await fault.WaitForPeersAsync(); }
            finally
            {
                fault.Dispose();
                await database.DisposeAsync();
            }
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DisposeAsync_PersistentDeadlock_PropagatesAfterThreeAttemptsAndCanBeRetried()
    {
        RequireCleanupEnabled();
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await CreateProbeAsync(database);
        using var fault = new CleanupSqlFault(database, failures: int.MaxValue);
        try
        {
            var failure = await Record.ExceptionAsync(() => database.DisposeAsync().AsTask());

            failure.Should().BeOfType<SqlException>().Which.Number.Should().Be(1205);
            fault.Attempts.Should().Be(3);
            (await DatabaseExistsAsync(database)).Should().BeTrue("failed cleanup must not be marked complete");
            await fault.WaitForPeersAsync();
            fault.Dispose();
            await database.DisposeAsync();
            (await DatabaseExistsAsync(database)).Should().BeFalse();
        }
        finally
        {
            try { await fault.WaitForPeersAsync(); }
            finally
            {
                fault.Dispose();
                await database.DisposeAsync();
            }
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DisposeAsync_NonDeadlockError_PropagatesWithoutRetry()
    {
        RequireCleanupEnabled();
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        using var fault = new CleanupSqlFault(database, failures: int.MaxValue, errorNumber: 51001);
        try
        {
            var failure = await Record.ExceptionAsync(() => database.DisposeAsync().AsTask());

            failure.Should().BeOfType<SqlException>().Which.Number.Should().Be(51001);
            fault.Attempts.Should().Be(1);
            (await DatabaseExistsAsync(database)).Should().BeTrue();
        }
        finally
        {
            fault.Dispose();
            await database.DisposeAsync();
        }
    }

    private static Task CreateProbeAsync(SqlServerTestDatabase database) => database.ExecuteNonQueryAsync("""
        CREATE TABLE dbo.CleanupRetryProbe (id INT PRIMARY KEY, value INT NOT NULL);
        INSERT INTO dbo.CleanupRetryProbe VALUES (1, 0), (2, 0);
        """);

    private static void RequireCleanupEnabled()
    {
        if (Environment.GetEnvironmentVariable("TRIPMATE_SQLSERVER_TEST_PRESERVE") == "1")
        {
            throw new InvalidOperationException("Cleanup regression tests require TRIPMATE_SQLSERVER_TEST_PRESERVE to be unset.");
        }
    }

    private static async Task<bool> DatabaseExistsAsync(SqlServerTestDatabase database)
    {
        var builder = new SqlConnectionStringBuilder(database.ConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.AddWithValue("@name", database.DatabaseName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    /// <summary>
    /// Two real SQL transactions deadlock on probe rows at the cleanup boundary before DROP.
    /// This verifies recovery, not reproduction of the original CI DDL/metadata lock cycle.
    /// Other databases/commands are untouched; no server-wide trigger or production seam is added.
    /// </summary>
    private sealed class CleanupSqlFault : IObserver<DiagnosticListener>, IDisposable
    {
        private readonly string _databaseName;
        private readonly string _connectionString;
        private readonly int _failures;
        private readonly int _errorNumber;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _allListeners;
        private readonly List<(SqlConnection Connection, SqlCommand Command, Task<int> Completion)> _peers = [];
        private string? _originalCommand;
        private int _attempts;
        private bool _disposed;

        public CleanupSqlFault(SqlServerTestDatabase database, int failures, int errorNumber = 1205)
        {
            _databaseName = database.DatabaseName;
            _connectionString = database.ConnectionString;
            _failures = failures;
            _errorNumber = errorNumber;
            _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public int Attempts => Volatile.Read(ref _attempts);

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != "SqlClientDiagnosticListener") return;
            lock (_subscriptions)
            {
                if (!_disposed) _subscriptions.Add(listener.Subscribe(new CommandObserver(this)));
            }
        }

        private void BeforeCommand(KeyValuePair<string, object?> item)
        {
            if (item.Key != "Microsoft.Data.SqlClient.WriteCommandBefore"
                || item.Value?.GetType().GetProperty("Command")?.GetValue(item.Value) is not SqlCommand command
                || !command.CommandText.Contains($"DROP DATABASE [{_databaseName}]", StringComparison.Ordinal)) return;

            _originalCommand ??= command.CommandText;
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt > _failures)
            {
                command.CommandText = "USE master;\n" + _originalCommand;
                return;
            }
            if (_errorNumber != 1205)
            {
                command.CommandText = $"THROW 51001, 'Injected non-deadlock cleanup failure.', 1; -- DROP DATABASE [{_databaseName}]";
                return;
            }

            StartDeadlockPeer();
            command.CommandText = $"""
                USE [{_databaseName}];
                SET DEADLOCK_PRIORITY LOW;
                BEGIN TRANSACTION;
                UPDATE dbo.CleanupRetryProbe SET value = 1 WHERE id = 1;
                UPDATE dbo.CleanupRetryProbe SET value = 1 WHERE id = 2;
                COMMIT;
                USE master;
                -- DROP DATABASE [{_databaseName}]
                """;
        }

        private void StartDeadlockPeer()
        {
            var connection = new SqlConnection(_connectionString);
            SqlCommand? command = null;
            try
            {
                connection.Open();
                using (var prepare = connection.CreateCommand())
                {
                    prepare.CommandText = """
                    UPDATE dbo.CleanupRetryProbe SET value = 0;
                    SET DEADLOCK_PRIORITY HIGH;
                    BEGIN TRANSACTION;
                    UPDATE dbo.CleanupRetryProbe SET value = 1 WHERE id = 2;
                    """;
                    prepare.ExecuteNonQuery();
                }
                command = connection.CreateCommand();
                command.CommandTimeout = 15;
                command.CommandText = """
                DECLARE @deadline DATETIME2 = DATEADD(SECOND, 10, SYSUTCDATETIME());
                WHILE (SELECT value FROM dbo.CleanupRetryProbe WITH (NOLOCK) WHERE id = 1) = 0
                BEGIN
                    IF SYSUTCDATETIME() >= @deadline THROW 51002, 'Cleanup deadlock probe did not start.', 1;
                    WAITFOR DELAY '00:00:00.010';
                END;
                UPDATE dbo.CleanupRetryProbe SET value = 1 WHERE id = 1;
                COMMIT;
                """;
                _peers.Add((connection, command, command.ExecuteNonQueryAsync()));
            }
            catch
            {
                command?.Dispose();
                connection.Dispose();
                throw;
            }
        }

        public Task WaitForPeersAsync() => Task.WhenAll(_peers.Select(peer => peer.Completion))
            .WaitAsync(TimeSpan.FromSeconds(15));

        public void OnError(Exception error) { }
        public void OnCompleted() { }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                if (_disposed) return;
                _disposed = true;
                _allListeners.Dispose();
                foreach (var subscription in _subscriptions) subscription.Dispose();
                foreach (var peer in _peers)
                {
                    peer.Command.Dispose();
                    peer.Connection.Dispose();
                }
            }
        }

        private sealed class CommandObserver(CleanupSqlFault owner) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnNext(KeyValuePair<string, object?> value) => owner.BeforeCommand(value);
            public void OnError(Exception error) { }
            public void OnCompleted() { }
        }
    }
}