using FluentAssertions;

using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class InMemoryPasswordResetAccountLockTests
{
    [Fact]
    public async Task ExecuteAsync_ForSameAccount_SerializesOperations()
    {
        var accountLock = new InMemoryPasswordResetAccountLock();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = accountLock.ExecuteAsync(
            42,
            async cancellationToken =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            },
            CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = accountLock.ExecuteAsync(
            42,
            _ =>
            {
                secondEntered.TrySetResult();
                return Task.CompletedTask;
            },
            CancellationToken.None);

        try
        {
            await Task.Delay(50);
            secondEntered.Task.IsCompleted.Should().BeFalse();
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        secondEntered.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenOperationThrows_ReleasesGate()
    {
        var accountLock = new InMemoryPasswordResetAccountLock();

        var failingOperation = async () => await accountLock.ExecuteAsync<int>(
            42,
            _ => Task.FromException<int>(new InvalidOperationException("test failure")),
            CancellationToken.None);

        await failingOperation.Should().ThrowAsync<InvalidOperationException>();

        var subsequentResult = await accountLock.ExecuteAsync(
                42,
                _ => Task.FromResult(7),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        subsequentResult.Should().Be(7);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWaitingIsCancelled_LeavesGateUsable()
    {
        var accountLock = new InMemoryPasswordResetAccountLock();
        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = accountLock.ExecuteAsync(
            42,
            async cancellationToken =>
            {
                holderEntered.TrySetResult();
                await releaseHolder.Task.WaitAsync(cancellationToken);
            },
            CancellationToken.None);
        await holderEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = accountLock.ExecuteAsync(
            42,
            _ => Task.CompletedTask,
            cancellation.Token);
        cancellation.Cancel();

        try
        {
            var cancelledOperation = async () => await cancelledWaiter;
            await cancelledOperation.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            releaseHolder.TrySetResult();
        }

        await holder.WaitAsync(TimeSpan.FromSeconds(2));
        var subsequentOperation = accountLock.ExecuteAsync(
            42,
            _ => Task.CompletedTask,
            CancellationToken.None);
        await subsequentOperation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_WhenAcquiredOperationIsCancelled_ReleasesGate()
    {
        var accountLock = new InMemoryPasswordResetAccountLock();
        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var cancelledOperation = accountLock.ExecuteAsync(
            42,
            async cancellationToken =>
            {
                operationEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            cancellation.Token);
        await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        var cancellationResult = async () => await cancelledOperation;
        await cancellationResult.Should().ThrowAsync<OperationCanceledException>();

        var subsequentOperation = accountLock.ExecuteAsync(
            42,
            _ => Task.CompletedTask,
            CancellationToken.None);
        await subsequentOperation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_ForManyAccounts_KeepsAllocatedGateCountFixed()
    {
        var accountLock = new InMemoryPasswordResetAccountLock();
        var initialGateCount = accountLock.AllocatedGateCount;

        for (var userId = 1L; userId <= 10_000; userId++)
        {
            await accountLock.ExecuteAsync(
                userId,
                _ => Task.CompletedTask,
                CancellationToken.None);
        }

        accountLock.AllocatedGateCount.Should().Be(initialGateCount);
        accountLock.AllocatedGateCount.Should().Be(InMemoryPasswordResetAccountLock.DefaultStripeCount);
    }
}