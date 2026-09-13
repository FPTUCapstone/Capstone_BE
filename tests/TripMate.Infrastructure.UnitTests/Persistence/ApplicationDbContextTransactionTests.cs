using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class ApplicationDbContextTransactionTests
{
    [Fact]
    public async Task ExecuteInTransactionAsync_ForwardsCancellationTokenThroughTransactionFlow()
    {
        RecordingExecutionStrategy.CapturedCancellationToken = default;

        await using var context = CreateContext();
        using var cancellationSource = new CancellationTokenSource();

        await context.ExecuteInTransactionAsync(
            _ => Task.FromResult(42),
            cancellationSource.Token);

        RecordingExecutionStrategy.CapturedCancellationToken
            .Should().Be(cancellationSource.Token);

        var transactionManager = context.GetService<IDbContextTransactionManager>()
            .Should().BeOfType<RecordingTransactionManager>().Subject;
        transactionManager.BeginCancellationToken.Should().Be(cancellationSource.Token);
        transactionManager.Transaction.CommitCancellationToken
            .Should().Be(cancellationSource.Token);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_WhenOperationFails_DoesNotCommitTransaction()
    {
        await using var context = CreateContext();
        var expectedException = new InvalidOperationException("Persistence failed.");

        var action = () => context.ExecuteInTransactionAsync<int>(
            _ => Task.FromException<int>(expectedException),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .Where(exception => ReferenceEquals(exception, expectedException));

        var transaction = context.GetService<IDbContextTransactionManager>()
            .Should().BeOfType<RecordingTransactionManager>().Subject.Transaction;
        transaction.Committed.Should().BeFalse();
        transaction.Disposed.Should().BeTrue();
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(new SqlConnection())
            .ReplaceService<IExecutionStrategyFactory, RecordingExecutionStrategyFactory>()
            .ReplaceService<IDbContextTransactionManager, RecordingTransactionManager>()
            .Options;

        return new ApplicationDbContext(options);
    }

    private sealed class RecordingExecutionStrategyFactory(
        ExecutionStrategyDependencies dependencies)
        : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new RecordingExecutionStrategy(
            dependencies.CurrentContext.Context);
    }

    private sealed class RecordingExecutionStrategy(DbContext context) : IExecutionStrategy
    {
        public static CancellationToken CapturedCancellationToken { get; set; }

        public bool RetriesOnFailure => false;

        public TResult Execute<TState, TResult>(
            TState state,
            Func<DbContext, TState, TResult> operation,
            Func<DbContext, TState, ExecutionResult<TResult>>? verifySucceeded) =>
            default!;

        public Task<TResult> ExecuteAsync<TState, TResult>(
            TState state,
            Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
            Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>?
                verifySucceeded,
            CancellationToken cancellationToken = default)
        {
            CapturedCancellationToken = cancellationToken;
            return operation(context, state, cancellationToken);
        }
    }

    private sealed class RecordingTransactionManager : IDbContextTransactionManager
    {
        public RecordingDbContextTransaction Transaction { get; } = new();

        public CancellationToken BeginCancellationToken { get; private set; }

        public IDbContextTransaction? CurrentTransaction { get; private set; }

        public IDbContextTransaction BeginTransaction()
        {
            CurrentTransaction = Transaction;
            return Transaction;
        }

        public Task<IDbContextTransaction> BeginTransactionAsync(
            CancellationToken cancellationToken = default)
        {
            BeginCancellationToken = cancellationToken;
            CurrentTransaction = Transaction;
            return Task.FromResult<IDbContextTransaction>(Transaction);
        }

        public void CommitTransaction() => Transaction.Commit();

        public Task CommitTransactionAsync(CancellationToken cancellationToken = default) =>
            Transaction.CommitAsync(cancellationToken);

        public void RollbackTransaction() => Transaction.Rollback();

        public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) =>
            Transaction.RollbackAsync(cancellationToken);

        public void ResetState() => CurrentTransaction = null;

        public Task ResetStateAsync(CancellationToken cancellationToken = default)
        {
            CurrentTransaction = null;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDbContextTransaction : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();

        public bool Committed { get; private set; }

        public bool Disposed { get; private set; }

        public CancellationToken CommitCancellationToken { get; private set; }

        public bool SupportsSavepoints => false;

        public void Commit() => Committed = true;

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCancellationToken = cancellationToken;
            Committed = true;
            return Task.CompletedTask;
        }

        public void Rollback()
        {
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void CreateSavepoint(string name) => throw new NotSupportedException();

        public Task CreateSavepointAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());

        public void RollbackToSavepoint(string name) => throw new NotSupportedException();

        public Task RollbackToSavepointAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());

        public void ReleaseSavepoint(string name) => throw new NotSupportedException();

        public Task ReleaseSavepointAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}