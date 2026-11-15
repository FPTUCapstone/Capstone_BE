using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Bookings;

public sealed class RefundManualCompletionLockSqlServerTests
{
    [SqlServerFact]
    public async Task SameRefundId_SerializesManualSettlementTransactions()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await using var firstContext = database.CreateDbContext();
        await using var secondContext = database.CreateDbContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task HoldFirstLockAsync()
        {
            await using var transaction = await firstContext.Database.BeginTransactionAsync(timeout.Token);
            await new SqlServerRefundManualCompletionLock(firstContext).AcquireAsync(91, timeout.Token);
            firstAcquired.TrySetResult(true);
            await releaseFirst.Task.WaitAsync(timeout.Token);
            await transaction.CommitAsync(timeout.Token);
        }

        async Task AcquireSecondLockAsync()
        {
            await firstAcquired.Task.WaitAsync(timeout.Token);
            await using var transaction = await secondContext.Database.BeginTransactionAsync(timeout.Token);
            await new SqlServerRefundManualCompletionLock(secondContext).AcquireAsync(91, timeout.Token);
            secondAcquired.TrySetResult(true);
            await transaction.RollbackAsync(timeout.Token);
        }

        Task first = HoldFirstLockAsync();
        Task second = AcquireSecondLockAsync();
        await firstAcquired.Task.WaitAsync(timeout.Token);

        Task completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(300), timeout.Token));
        completed.Should().NotBe(second, "the same refund must remain locked until the first completion commits");

        releaseFirst.TrySetResult(true);
        await Task.WhenAll(first, second);
        secondAcquired.Task.IsCompleted.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task AcquireWithoutTransaction_IsRejected()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await using var context = database.CreateDbContext();

        var action = () => new SqlServerRefundManualCompletionLock(context).AcquireAsync(91, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*caller-owned transaction*");
    }
}