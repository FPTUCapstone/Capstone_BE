using System.Diagnostics;

using FluentAssertions;

using Microsoft.Data.SqlClient;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class SqlServerTestDatabaseConnectionTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateAsync_BootstrapReusesTwoConnectionsEvenWhenOpeningIsRetried()
    {
        using var probe = new ConnectionProbe(openFailures: 1);
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var bootstrapConnections = probe.DistinctConnections;
        probe.OpenAttempts.Should().BeGreaterThan(2, "the injected opening timeout must be retried");
        probe.Dispose();

        bootstrapConnections.Should().Be(2,
            "schema and ordered migrations should share one database connection instead of reconnecting for every file");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'AuditLogs';"))
            .Should().Be(1);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteScriptAsync_OpenTimeout_RecoversBeforeExecutingTheScript(int failures)
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var path = Path.Combine(Path.GetTempPath(), $"tripmate-connection-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, "CREATE TABLE dbo.ConnectionRetryProbe (value INT NOT NULL);\nGO\nINSERT dbo.ConnectionRetryProbe VALUES (1);");
        try
        {
            using var probe = new ConnectionProbe(database.DatabaseName, openFailures: failures);
            var failure = await Record.ExceptionAsync(() => database.ExecuteScriptAsync(path));

            if (failure is not null) failure.Should().BeOfType<TimeoutException>();
            failure.Should().BeNull("a transient failure before opening the SQL connection must be recoverable");
            probe.OpenAttempts.Should().BeInRange(failures + 1, 3);
            probe.DistinctConnections.Should().Be(1);
            probe.Dispose();
            (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ConnectionRetryProbe;"))
                .Should().Be(1, "the script is executed only after the connection is open");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExecuteScriptAsync_PersistentOpenTimeout_StopsAfterThreeAttempts()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var path = Path.Combine(Path.GetTempPath(), $"tripmate-connection-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, "CREATE TABLE dbo.ConnectionRetryProbe (value INT NOT NULL);");
        try
        {
            using var probe = new ConnectionProbe(database.DatabaseName, openFailures: int.MaxValue);
            var failure = await Record.ExceptionAsync(() => database.ExecuteScriptAsync(path));

            failure.Should().BeOfType<TimeoutException>();
            probe.OpenAttempts.Should().Be(3);
            probe.Dispose();
            (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'ConnectionRetryProbe';"))
                .Should().Be(0, "no batch may execute when opening never succeeds");
        }
        finally { File.Delete(path); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExecuteScriptAsync_ExecutionFailure_DoesNotReplayCommittedBatches()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var path = Path.Combine(Path.GetTempPath(), $"tripmate-connection-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, """
            CREATE TABLE dbo.ConnectionRetryProbe (value INT NOT NULL);
            INSERT dbo.ConnectionRetryProbe VALUES (1);
            GO
            THROW 51001, 'CI787: failure after a previous batch committed.', 1;
            """);
        try
        {
            using var probe = new ConnectionProbe(database.DatabaseName);
            var failure = await Record.ExceptionAsync(() => database.ExecuteScriptAsync(path));

            failure.Should().BeOfType<SqlException>().Which.Number.Should().Be(51001);
            probe.DistinctConnections.Should().Be(1);
            probe.Dispose();
            (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ConnectionRetryProbe;"))
                .Should().Be(1, "the first committed batch must not be executed again");
        }
        finally { File.Delete(path); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExecuteScalarAsync_NonTimeoutOpenFailure_PropagatesWithoutRetry()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var error = new InvalidOperationException("CI787: non-transient opening failure.");
        using var probe = new ConnectionProbe(database.DatabaseName, openFailures: int.MaxValue, openError: error);

        var failure = await Record.ExceptionAsync(() => database.ExecuteScalarAsync<int>("SELECT 1;"));

        failure.Should().BeSameAs(error);
        probe.OpenAttempts.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExecuteScalarAsync_CancelledAfterOpenTimeout_DoesNotRetry()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        using var cancellation = new CancellationTokenSource();
        using var probe = new ConnectionProbe(database.DatabaseName, openFailures: 1, beforeOpenFailure: cancellation.Cancel);

        var failure = await Record.ExceptionAsync(() => database.ExecuteScalarAsync<int>("SELECT 1;", cancellation.Token));

        failure.Should().BeAssignableTo<OperationCanceledException>();
        probe.OpenAttempts.Should().Be(1);
    }

    /// <summary>
    /// Injects a CLR timeout at SqlClient's connection-open boundary. This checks policy,
    /// not reproduction of CI's non-pooled connection pressure. The SQL script is real.
    /// AsyncLocal limits observation to this test's execution flow and its temporary DB.
    /// </summary>
    private sealed class ConnectionProbe : IObserver<DiagnosticListener>, IDisposable
    {
        private readonly AsyncLocal<bool> _active = new();
        private readonly string? _databaseName;
        private readonly int _openFailures;
        private readonly Exception _openError;
        private readonly Action? _beforeOpenFailure;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly HashSet<SqlConnection> _connections = new(ReferenceEqualityComparer.Instance);
        private readonly IDisposable _listeners;
        private int _openAttempts;
        private bool _disposed;

        public ConnectionProbe(string? databaseName = null, int openFailures = 0,
            Exception? openError = null, Action? beforeOpenFailure = null)
        {
            _databaseName = databaseName;
            _openFailures = openFailures;
            _openError = openError ?? new TimeoutException("CI787: injected timeout before opening the SQL connection.");
            _beforeOpenFailure = beforeOpenFailure;
            _active.Value = true;
            _listeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public int OpenAttempts => Volatile.Read(ref _openAttempts);
        public int DistinctConnections
        {
            get { lock (_subscriptions) return _connections.Count; }
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != "SqlClientDiagnosticListener") return;
            lock (_subscriptions)
            {
                if (!_disposed) _subscriptions.Add(listener.Subscribe(new Observer(this)));
            }
        }

        private void Observe(KeyValuePair<string, object?> item)
        {
            if (_disposed || !_active.Value) return;
            if (item.Key != "Microsoft.Data.SqlClient.WriteConnectionOpenBefore"
                || item.Value?.GetType().GetProperty("Connection")?.GetValue(item.Value) is not SqlConnection connection
                || (_databaseName is not null && connection.Database != _databaseName)) return;

            lock (_subscriptions) _connections.Add(connection);
            if (Interlocked.Increment(ref _openAttempts) <= _openFailures)
            {
                _beforeOpenFailure?.Invoke();
                throw _openError;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }

        public void Dispose()
        {
            IDisposable[] subscriptions;
            lock (_subscriptions)
            {
                if (_disposed) return;
                _disposed = true;
                subscriptions = _subscriptions.ToArray();
            }
            _active.Value = false;
            _listeners.Dispose();
            foreach (var subscription in subscriptions) subscription.Dispose();
        }

        private sealed class Observer(ConnectionProbe owner) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnNext(KeyValuePair<string, object?> value) => owner.Observe(value);
            public void OnError(Exception error) { }
            public void OnCompleted() { }
        }
    }
}