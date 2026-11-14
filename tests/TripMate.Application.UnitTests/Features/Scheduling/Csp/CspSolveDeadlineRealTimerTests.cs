using System.Diagnostics;
using System.Runtime.ExceptionServices;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Csp;

using Xunit.Abstractions;

namespace TripMate.Application.UnitTests.Features.Scheduling.Csp;

public sealed class CspSolveDeadlineRealTimerTests(ITestOutputHelper output)
{
    private const int SampleCount = 20;
    private const int DueMilliseconds = 200;
    private static readonly TimeSpan StrictCeiling = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SystemDeadline_SignalsEverySample_UnderBoundedThreadPoolContention()
    {
        var observedMilliseconds = new double[SampleCount];
        const int blockerCount = 1;

        for (var sample = 0; sample < SampleCount; sample++)
        {
            using var releaseBlockers = new ManualResetEventSlim(false);
            var blockers = await StartRunningBlockersAsync(blockerCount, releaseBlockers);

            var workersBefore = 0;
            var ioBefore = 0;
            long pendingBefore = 0;
            var startedBeforeTimer = 0;
            var queuedBeforeTimer = 0;
            var outstandingBeforeTimer = 0;
            var clock = new Stopwatch();
            await RunWithGuaranteedBlockerCleanupAsync(
                async () =>
                {
                    var contention = ObserveBoundedContention(
                        blockerCount,
                        blockerCount,
                        blockers);
                    startedBeforeTimer = contention.Started;
                    queuedBeforeTimer = contention.Queued;
                    outstandingBeforeTimer = contention.Outstanding;

                    ThreadPool.GetAvailableThreads(out workersBefore, out ioBefore);
                    pendingBefore = ThreadPool.PendingWorkItemCount;
                    clock.Start();
                    using var deadline = new CspItinerarySolver.SolveDeadline(
                        clock,
                        DueMilliseconds,
                        CancellationToken.None);

                    var observedAt = await ObserveCancellationAsync(
                        deadline.Token,
                        clock,
                        StrictCeiling);
                    clock.Stop();

                    observedAt.Should().NotBeNull(
                        $"the real CancelAfter signal must arrive within {StrictCeiling.TotalSeconds:F0} seconds");
                    observedAt!.Value.Should().BeLessThanOrEqualTo(StrictCeiling,
                        "the callback timestamp, not continuation dispatch time, defines timer liveness");
                    deadline.Token.IsCancellationRequested.Should().BeTrue();
                    observedMilliseconds[sample] = observedAt.Value.TotalMilliseconds;
                },
                releaseBlockers,
                blockers);

            ThreadPool.GetAvailableThreads(out int workersAfter, out int ioAfter);
            var pendingAfter = ThreadPool.PendingWorkItemCount;
            output.WriteLine(
                "sample={0:D2}; due_ms={1}; observed_ms={2:F4}; overshoot_ms={3:F4}; "
                + "workers_before={4}; workers_after={5}; io_before={6}; io_after={7}; "
                + "pending_before={8}; pending_after={9}; blockers_started={10}; "
                + "blockers_queued={11}; blockers_outstanding={12}",
                sample + 1,
                DueMilliseconds,
                observedMilliseconds[sample],
                observedMilliseconds[sample] - DueMilliseconds,
                workersBefore,
                workersAfter,
                ioBefore,
                ioAfter,
                pendingBefore,
                pendingAfter,
                startedBeforeTimer,
                queuedBeforeTimer,
                outstandingBeforeTimer);
        }

        Array.Sort(observedMilliseconds);
        var p95 = observedMilliseconds[Math.Min(
            observedMilliseconds.Length - 1,
            (int)Math.Floor(observedMilliseconds.Length * 0.95))];
        var softTargetMet = p95 <= 1_000;

        output.WriteLine(
            "summary: samples={0}; due_ms={1}; min_ms={2:F4}; median_ms={3:F4}; p95_ms={4:F4}; "
            + "max_ms={5:F4}; soft_target_ms=1000; soft_target_met={6}",
            observedMilliseconds.Length,
            DueMilliseconds,
            observedMilliseconds[0],
            observedMilliseconds[observedMilliseconds.Length / 2],
            p95,
            observedMilliseconds[^1],
            softTargetMet);
    }

    private static async Task RunWithGuaranteedBlockerCleanupAsync(
        Func<Task> operation,
        ManualResetEventSlim releaseBlockers,
        IReadOnlyCollection<Task> blockers)
    {
        Exception? operationFailure = null;
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        var cleanupFailures = new List<Exception>();
        try
        {
            releaseBlockers.Set();
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }

        var allBlockers = Task.WhenAll(blockers);
        try
        {
            await allBlockers.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            if (allBlockers.Exception is { } blockerFailures)
            {
                cleanupFailures.AddRange(blockerFailures.InnerExceptions);
            }
            else
            {
                cleanupFailures.Add(exception);
            }
        }

        if (operationFailure is null && cleanupFailures.Count == 0)
        {
            return;
        }

        if (operationFailure is not null && cleanupFailures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (operationFailure is null && cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        }

        var failures = new List<Exception>(cleanupFailures.Count + 1);
        if (operationFailure is not null)
        {
            failures.Add(operationFailure);
        }

        failures.AddRange(cleanupFailures);
        throw new AggregateException(failures);
    }

    private static BoundedContentionSnapshot ObserveBoundedContention(
        int expectedBlockerCount,
        int startedCount,
        IReadOnlyCollection<Task> blockers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startedCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startedCount, expectedBlockerCount);

        if (blockers.Count != expectedBlockerCount)
        {
            throw new InvalidOperationException(
                $"expected {expectedBlockerCount} owned ThreadPool blockers, found {blockers.Count}");
        }

        var outstandingCount = blockers.Count(blocker => !blocker.IsCompleted);
        if (outstandingCount != expectedBlockerCount)
        {
            throw new InvalidOperationException(
                "every owned ThreadPool blocker must be outstanding before the production timer is scheduled");
        }

        if (startedCount != expectedBlockerCount)
        {
            throw new InvalidOperationException(
                $"every owned ThreadPool blocker must be physically running; "
                + $"started {startedCount} of {expectedBlockerCount}");
        }

        return new BoundedContentionSnapshot(
            startedCount,
            expectedBlockerCount - startedCount,
            outstandingCount);
    }

    private readonly record struct BoundedContentionSnapshot(
        int Started,
        int Queued,
        int Outstanding);

    private static async Task<Task[]> StartRunningBlockersAsync(
        int blockerCount,
        ManualResetEventSlim releaseBlockers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockerCount);

        var startedCount = 0;
        var allStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockers = Enumerable.Range(0, blockerCount)
            .Select(_ => Task.Run(() =>
            {
                if (Interlocked.Increment(ref startedCount) == blockerCount)
                {
                    allStarted.TrySetResult();
                }

                releaseBlockers.Wait();
            }))
            .ToArray();

        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return blockers;
        }
        catch (Exception startupFailure)
        {
            await RunWithGuaranteedBlockerCleanupAsync(
                () => Task.FromException(startupFailure),
                releaseBlockers,
                blockers);
            throw;
        }
    }

    private static async Task<TimeSpan?> ObserveCancellationAsync(
        CancellationToken token,
        Stopwatch clock,
        TimeSpan ceiling)
    {
        var observedAt = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(
            () => observedAt.TrySetResult(clock.Elapsed));

        try
        {
            return await observedAt.Task.WaitAsync(ceiling);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    [Fact]
    public async Task RunningBlockerStartup_ReturnsOnlyAfterOwnedWorkerIsPhysicallyRunning()
    {
        using var releaseBlockers = new ManualResetEventSlim(false);

        var blockers = await StartRunningBlockersAsync(1, releaseBlockers);

        blockers.Should().ContainSingle();
        blockers[0].IsCompleted.Should().BeFalse();

        releaseBlockers.Set();
        await Task.WhenAll(blockers).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationWait_ObservesRealTimerWithoutSynchronouslyBlockingTheCaller()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));

        var clock = Stopwatch.StartNew();
        var observedAt = await ObserveCancellationAsync(
            cancellation.Token,
            clock,
            TimeSpan.FromSeconds(5));

        observedAt.Should().NotBeNull();
        cancellation.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void ContentionObservation_AccountsForEveryRunningBlocker()
    {
        var blockers = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task)
            .ToArray();

        var snapshot = ObserveBoundedContention(4, 4, blockers);

        snapshot.Started.Should().Be(4);
        snapshot.Queued.Should().Be(0);
        snapshot.Outstanding.Should().Be(4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ContentionObservation_RejectsMerelyQueuedBlockers(int startedCount)
    {
        var blockers = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task)
            .ToArray();

        Action act = () => ObserveBoundedContention(4, startedCount, blockers);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be physically running*");
    }

    [Fact]
    public void ContentionObservation_RejectsACompletedBlocker()
    {
        Task[] blockers =
        [
            Task.CompletedTask,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task,
        ];

        Action act = () => ObserveBoundedContention(2, 1, blockers);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*every owned ThreadPool blocker must be outstanding*");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void ContentionObservation_RejectsAnImpossibleStartedCount(int startedCount)
    {
        var blockers = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task)
            .ToArray();

        Action act = () => ObserveBoundedContention(4, startedCount, blockers);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task BlockerCleanup_ReleasesAndAwaitsWorkers_AfterSuccessfulOperation()
    {
        using var releaseBlockers = new ManualResetEventSlim(false);
        var blocker = Task.Run(releaseBlockers.Wait);
        var operationCompleted = false;

        await RunWithGuaranteedBlockerCleanupAsync(
            () =>
            {
                operationCompleted = true;
                return Task.CompletedTask;
            },
            releaseBlockers,
            [blocker]);

        operationCompleted.Should().BeTrue();
        releaseBlockers.IsSet.Should().BeTrue();
        blocker.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task BlockerCleanup_ReleasesAndAwaitsWorkers_WhenStartupValidationFails()
    {
        using var releaseBlockers = new ManualResetEventSlim(false);
        var blocker = Task.Run(releaseBlockers.Wait);

        Func<Task> act = () => RunWithGuaranteedBlockerCleanupAsync(
            () =>
            {
                false.Should().BeTrue("startup validation failed");
                return Task.CompletedTask;
            },
            releaseBlockers,
            [blocker]);

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*startup validation failed*");
        releaseBlockers.IsSet.Should().BeTrue();
        blocker.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task BlockerCleanup_ObservesWorkerFault_AfterReleasingEveryWorker()
    {
        using var releaseBlockers = new ManualResetEventSlim(false);
        var blocker = Task.Run(() =>
        {
            releaseBlockers.Wait();
            throw new InvalidOperationException("worker fault");
        });

        Func<Task> act = () => RunWithGuaranteedBlockerCleanupAsync(
            () => Task.CompletedTask,
            releaseBlockers,
            [blocker]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("worker fault");
        releaseBlockers.IsSet.Should().BeTrue();
        blocker.IsFaulted.Should().BeTrue();
    }

    [Fact]
    public async Task BlockerCleanup_RetainsPrimaryAndWorkerFailures()
    {
        using var releaseBlockers = new ManualResetEventSlim(false);
        var blocker = Task.Run(() =>
        {
            releaseBlockers.Wait();
            throw new InvalidOperationException("worker fault");
        });

        Func<Task> act = () => RunWithGuaranteedBlockerCleanupAsync(
            () =>
            {
                false.Should().BeTrue("startup validation failed");
                return Task.CompletedTask;
            },
            releaseBlockers,
            [blocker]);

        var failure = await act.Should().ThrowAsync<AggregateException>();
        failure.Which.InnerExceptions.Should().Contain(exception =>
            exception.Message.Contains("startup validation failed", StringComparison.Ordinal));
        failure.Which.InnerExceptions.Should().Contain(exception =>
            exception.Message == "worker fault");
        releaseBlockers.IsSet.Should().BeTrue();
        blocker.IsFaulted.Should().BeTrue();
    }

    [Fact]
    public void SystemDeadline_DisposalCancelsThePendingTimer()
    {
        using var callbackObserved = new ManualResetEventSlim(false);
        var clock = Stopwatch.StartNew();
        var deadline = new CspItinerarySolver.SolveDeadline(
            clock,
            DueMilliseconds,
            CancellationToken.None);
        using var registration = deadline.Token.Register(callbackObserved.Set);

        deadline.Dispose();

        callbackObserved.Wait(TimeSpan.FromMilliseconds(DueMilliseconds * 3)).Should().BeFalse(
            "disposing the deadline must release its pending timer without a late callback");
    }
}