using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class ApplicationDbContextTransactionTests
{
    [Fact]
    public async Task ExecuteInTransactionAsync_ForwardsCancellationTokenToExecutionStrategy()
    {
        RecordingExecutionStrategy.CapturedCancellationToken = default;

        await using var context = CreateContext();
        using var cancellationSource = new CancellationTokenSource();

        await context.ExecuteInTransactionAsync(
            _ => Task.FromResult(42),
            cancellationSource.Token);

        RecordingExecutionStrategy.CapturedCancellationToken
            .Should().Be(cancellationSource.Token);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(new SqlConnection())
            .ReplaceService<IExecutionStrategyFactory, RecordingExecutionStrategyFactory>()
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
            _ = context;
            CapturedCancellationToken = cancellationToken;
            return Task.FromResult(default(TResult)!);
        }
    }
}
