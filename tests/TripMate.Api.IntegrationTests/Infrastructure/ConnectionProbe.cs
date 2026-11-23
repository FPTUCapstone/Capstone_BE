using System.Diagnostics;

using Microsoft.Data.SqlClient;

namespace TripMate.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Injects a CLR timeout at SqlClient's connection-open boundary. This checks policy,
/// not reproduction of CI's non-pooled connection pressure. The SQL script is real.
/// AsyncLocal limits observation to this test's execution flow and its temporary DB.
/// </summary>
/// <remarks>Never attach it around the first use of Shared; use your own SqlServerTemplateDatabase instance.</remarks>
internal sealed class ConnectionProbe : IObserver<DiagnosticListener>, IDisposable
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