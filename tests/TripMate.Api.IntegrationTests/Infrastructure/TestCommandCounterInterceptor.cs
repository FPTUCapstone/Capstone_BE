using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TripMate.Api.IntegrationTests.Infrastructure;

internal sealed class TestCommandCounterInterceptor : DbCommandInterceptor
{
    private int _commandCount;
    private readonly List<string> _commandTexts = [];
    private readonly object _lock = new();

    public int CommandCount
    {
        get
        {
            lock (_lock)
            {
                return _commandCount;
            }
        }
    }

    public IReadOnlyList<string> CommandTexts
    {
        get
        {
            lock (_lock)
            {
                return _commandTexts.ToArray();
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _commandCount = 0;
            _commandTexts.Clear();
        }
    }

    private void Record(DbCommand command)
    {
        lock (_lock)
        {
            _commandCount++;
            _commandTexts.Add(command.CommandText);
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Record(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}