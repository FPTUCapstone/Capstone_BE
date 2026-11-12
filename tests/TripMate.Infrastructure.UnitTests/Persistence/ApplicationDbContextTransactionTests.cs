using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

using TripMate.Application.Common.Locks;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class ApplicationDbContextTransactionTests
{
    [Fact]
    public async Task ExecuteInNonRetryingTransactionAsync_ForwardsCancellationTokenAndCommitsOnce()
    {
        await using var context = CreateContext();
        using var cancellationSource = new CancellationTokenSource();
        var operationCalls = 0;

        var result = await context.ExecuteInNonRetryingTransactionAsync(
            token =>
            {
                token.Should().Be(cancellationSource.Token);
                operationCalls++;
                return Task.FromResult(42);
            },
            cancellationSource.Token);

        result.Should().Be(42);
        operationCalls.Should().Be(1);
        var manager = GetTransactionManager(context);
        manager.BeginCancellationToken.Should().Be(cancellationSource.Token);
        manager.Transaction.CommitCalls.Should().Be(1);
        manager.Transaction.CommitCancellationToken.Should().Be(cancellationSource.Token);
        manager.Transaction.RollbackCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteInNonRetryingTransactionAsync_WhenStrategyRetries_RejectsBeforeTransactionStarts()
    {
        await using var context = CreateContext(retriesOnFailure: true);
        var operationCalls = 0;

        var action = () => context.ExecuteInNonRetryingTransactionAsync(
            _ =>
            {
                operationCalls++;
                return Task.FromResult(42);
            },
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*retry*");
        operationCalls.Should().Be(0);
        GetTransactionManager(context).BeginCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteInNonRetryingTransactionAsync_WhenOperationFails_RollsBackAndDoesNotCommit()
    {
        await using var context = CreateContext();
        var expectedException = new InvalidOperationException("Persistence failed.");

        var action = () => context.ExecuteInNonRetryingTransactionAsync<int>(
            _ => Task.FromException<int>(expectedException),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .Where(exception => ReferenceEquals(exception, expectedException));
        var transaction = GetTransactionManager(context).Transaction;
        transaction.CommitCalls.Should().Be(0);
        transaction.RollbackCalls.Should().Be(1);
        transaction.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteInNonRetryingTransactionAsync_WhenCanceled_RollsBackWithCleanupToken()
    {
        await using var context = CreateContext();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var action = () => context.ExecuteInNonRetryingTransactionAsync<int>(
            token => Task.FromCanceled<int>(token),
            cancellationSource.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        var transaction = GetTransactionManager(context).Transaction;
        transaction.RollbackCalls.Should().Be(1);
        transaction.RollbackCancellationToken.Should().Be(CancellationToken.None);
        transaction.CommitCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteInNonRetryingTransactionAsync_WhenCommitFails_ClassifiesUnknownOutcomeWithoutReplay()
    {
        await using var context = CreateContext();
        var expectedException = new InvalidOperationException("Commit outcome is unknown.");
        var operationCalls = 0;
        var transaction = GetTransactionManager(context).Transaction;
        transaction.CommitException = expectedException;

        var action = () => context.ExecuteInNonRetryingTransactionAsync(
            _ =>
            {
                operationCalls++;
                return Task.FromResult(42);
            },
            CancellationToken.None);

        var failure = await action.Should().ThrowAsync<TransactionLockAcquisitionException>();
        failure.Which.FailureKind.Should().Be(TransactionLockFailureKind.CommitOutcomeUnknown);
        failure.Which.ResourceCategory.Should().Be("TransactionCommit");
        failure.Which.InnerException.Should().BeSameAs(expectedException);
        operationCalls.Should().Be(1);
        transaction.CommitCalls.Should().Be(1);
        transaction.RollbackCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteInNonRetryingTransactionAsync_WhenRollbackFails_PreservesOperationException()
    {
        await using var context = CreateContext();
        var operationException = new InvalidOperationException("Operation failed.");
        var rollbackException = new InvalidOperationException("Rollback failed.");
        var transaction = GetTransactionManager(context).Transaction;
        transaction.RollbackException = rollbackException;

        var action = () => context.ExecuteInNonRetryingTransactionAsync<int>(
            _ => Task.FromException<int>(operationException),
            CancellationToken.None);

        var thrown = await action.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(operationException);
        thrown.Which.Data["RollbackException"].Should().BeSameAs(rollbackException);
        transaction.CommitCalls.Should().Be(0);
        transaction.RollbackCalls.Should().Be(1);
    }

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

    private static ApplicationDbContext CreateContext(bool retriesOnFailure = false)
    {
        RecordingExecutionStrategy.Retries = retriesOnFailure;
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(new SqlConnection())
            .ReplaceService<IExecutionStrategyFactory, RecordingExecutionStrategyFactory>()
            .ReplaceService<IDbContextTransactionManager, RecordingTransactionManager>()
            .Options;

        return new ApplicationDbContext(options);
    }

    private static RecordingTransactionManager GetTransactionManager(ApplicationDbContext context) =>
        context.GetService<IDbContextTransactionManager>()
            .Should().BeOfType<RecordingTransactionManager>().Subject;

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

        public static bool Retries { get; set; }

        public bool RetriesOnFailure => Retries;

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

        public int BeginCalls { get; private set; }

        public IDbContextTransaction? CurrentTransaction { get; private set; }

        public IDbContextTransaction BeginTransaction()
        {
            CurrentTransaction = Transaction;
            return Transaction;
        }

        public Task<IDbContextTransaction> BeginTransactionAsync(
            CancellationToken cancellationToken = default)
        {
            BeginCalls++;
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

        public int CommitCalls { get; private set; }

        public int RollbackCalls { get; private set; }

        public Exception? CommitException { get; set; }

        public Exception? RollbackException { get; set; }

        public bool Disposed { get; private set; }

        public CancellationToken CommitCancellationToken { get; private set; }

        public CancellationToken RollbackCancellationToken { get; private set; }

        public bool SupportsSavepoints => false;

        public void Commit()
        {
            CommitCalls++;
            if (CommitException is not null)
            {
                throw CommitException;
            }

            Committed = true;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            CommitCancellationToken = cancellationToken;
            if (CommitException is not null)
            {
                return Task.FromException(CommitException);
            }

            Committed = true;
            return Task.CompletedTask;
        }

        public void Rollback()
        {
            RollbackCalls++;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            RollbackCancellationToken = cancellationToken;
            return RollbackException is null
                ? Task.CompletedTask
                : Task.FromException(RollbackException);
        }

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