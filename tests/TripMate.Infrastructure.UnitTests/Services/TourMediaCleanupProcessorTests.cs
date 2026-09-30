using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class TourMediaCleanupProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TourMediaStorageDeleteOutcome.Deleted)]
    [InlineData(TourMediaStorageDeleteOutcome.AlreadyAbsent)]
    public async Task ProcessDueBatchAsync_DeletedOrAbsent_CompletesOutbox(
        TourMediaStorageDeleteOutcome outcome)
    {
        var store = new RecordingCleanupStore(CreateClaim());
        var storage = new FakeMediaStorage(new TourMediaStorageDeleteResult(outcome, null));
        var processor = CreateProcessor(store, storage, new FixedClock(Now));

        var processed = await processor.ProcessDueBatchAsync(
            TimeSpan.FromMinutes(2), 10, CancellationToken.None);

        processed.Should().Be(1);
        store.Completed.Should().ContainSingle().Which.Should().Be((41, ClaimToken, Now));
        store.Retried.Should().BeEmpty();
        store.Exhausted.Should().BeEmpty();
        storage.LastPublicId.Should().Be(ClaimPublicId);
    }

    [Fact]
    public async Task ProcessDueBatchAsync_TransientFailure_SchedulesExponentialRetry()
    {
        var store = new RecordingCleanupStore(CreateClaim(attemptCount: 3));
        var storage = new FakeMediaStorage(new TourMediaStorageDeleteResult(
            TourMediaStorageDeleteOutcome.TransientFailure,
            "SAFE_TRANSIENT_CODE"));
        var processor = CreateProcessor(store, storage, new FixedClock(Now));

        await processor.ProcessDueBatchAsync(TimeSpan.FromMinutes(2), 10, CancellationToken.None);

        store.Retried.Should().ContainSingle().Which.Should().Be((
            41,
            ClaimToken,
            "SAFE_TRANSIENT_CODE",
            Now.AddMinutes(4),
            Now));
        store.Exhausted.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessDueBatchAsync_PermanentFailure_ExhaustsImmediatelyWithoutProviderDetails()
    {
        var store = new RecordingCleanupStore(CreateClaim());
        var storage = new FakeMediaStorage(new TourMediaStorageDeleteResult(
            TourMediaStorageDeleteOutcome.PermanentFailure,
            "SAFE_REJECTION_CODE"));
        var processor = CreateProcessor(store, storage, new FixedClock(Now));

        await processor.ProcessDueBatchAsync(TimeSpan.FromMinutes(2), 10, CancellationToken.None);

        store.Exhausted.Should().ContainSingle().Which.Should().Be((
            41,
            ClaimToken,
            "SAFE_REJECTION_CODE",
            Now));
        store.Retried.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessDueBatchAsync_UnclassifiedErrorCode_UsesConstantSafeCode()
    {
        var store = new RecordingCleanupStore(CreateClaim());
        var storage = new FakeMediaStorage(new TourMediaStorageDeleteResult(
            TourMediaStorageDeleteOutcome.TransientFailure,
            "raw provider response with credential"));
        var processor = CreateProcessor(store, storage, new FixedClock(Now));

        await processor.ProcessDueBatchAsync(TimeSpan.FromMinutes(2), 10, CancellationToken.None);

        store.Retried.Should().ContainSingle();
        store.Retried[0].ErrorCode.Should().Be("TOUR_MEDIA_CLEANUP_UNCLASSIFIED");
        store.Retried[0].ErrorCode.Should().NotContain("credential");
    }

    [Fact]
    public async Task ProcessDueBatchAsync_FinalTransientAttempt_ExhaustsInsteadOfSchedulingRetry()
    {
        var store = new RecordingCleanupStore(CreateClaim(attemptCount: 8, maxAttempts: 8));
        var storage = new FakeMediaStorage(new TourMediaStorageDeleteResult(
            TourMediaStorageDeleteOutcome.TransientFailure,
            "SAFE_TRANSIENT_CODE"));
        var processor = CreateProcessor(store, storage, new FixedClock(Now));

        await processor.ProcessDueBatchAsync(TimeSpan.FromMinutes(2), 10, CancellationToken.None);

        store.Exhausted.Should().ContainSingle();
        store.Retried.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessDueBatchAsync_ClaimsNextItemOnlyAfterCurrentProviderCallFinishes()
    {
        var claims = new Queue<TourMediaCleanupClaim>([
            CreateClaim(),
            CreateClaim(id: 42, publicId: "tripmate/tours/42/second-media"),
        ]);
        var store = new SequencedCleanupStore(claims);
        var storage = new BlockingMediaStorage();
        var processor = CreateProcessor(store, storage, new FixedClock(Now));

        Task<int> processing = processor.ProcessDueBatchAsync(
            TimeSpan.FromMinutes(2), 2, CancellationToken.None);
        await storage.FirstDestroyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            store.ClaimCalls.Should().Be(1);
            storage.DestroyCalls.Should().Be(1);
        }
        finally
        {
            storage.ReleaseFirstDestroy();
        }

        (await processing).Should().Be(2);
        store.ClaimCalls.Should().Be(2, "each returned claim is requested separately and the queue ends after the second item");
        storage.DestroyCalls.Should().Be(2);
        store.Completed.Select(item => item.Id).Should().Equal(41, 42);
    }

    private static TourMediaCleanupProcessor CreateProcessor(
        ITourMediaCleanupOutboxStore store,
        ITourMediaStorage storage,
        IDateTimeProvider clock) =>
        new(store, storage, clock, NullLogger<TourMediaCleanupProcessor>.Instance);

    private static TourMediaCleanupClaim CreateClaim(
        int attemptCount = 1,
        int maxAttempts = 8,
        long id = 41,
        string publicId = ClaimPublicId) =>
        new(id, publicId, ClaimToken, attemptCount, maxAttempts);

    private const string ClaimPublicId = "tripmate/tours/42/media";
    private static readonly Guid ClaimToken = Guid.Parse("baddcafe-0000-4000-8000-000000000001");

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RecordingCleanupStore(TourMediaCleanupClaim claim) : ITourMediaCleanupOutboxStore
    {
        private bool claimAvailable = true;

        public List<(long Id, Guid Token, DateTimeOffset At)> Completed { get; } = [];
        public List<(long Id, Guid Token, string ErrorCode, DateTimeOffset NotBefore, DateTimeOffset At)> Retried { get; } = [];
        public List<(long Id, Guid Token, string ErrorCode, DateTimeOffset At)> Exhausted { get; } = [];

        public Task<IReadOnlyList<TourMediaCleanupClaim>> ClaimDueAsync(
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int batchSize,
            CancellationToken cancellationToken)
        {
            if (!claimAvailable)
            {
                return Task.FromResult<IReadOnlyList<TourMediaCleanupClaim>>([]);
            }

            claimAvailable = false;
            return Task.FromResult<IReadOnlyList<TourMediaCleanupClaim>>([claim]);
        }

        public Task CompleteAsync(long id, Guid leaseToken, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
        {
            Completed.Add((id, leaseToken, completedAtUtc));
            return Task.CompletedTask;
        }

        public Task RetryAsync(
            long id,
            Guid leaseToken,
            string safeErrorCode,
            DateTimeOffset notBeforeUtc,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken)
        {
            Retried.Add((id, leaseToken, safeErrorCode, notBeforeUtc, updatedAtUtc));
            return Task.CompletedTask;
        }

        public Task ExhaustAsync(long id, Guid leaseToken, string safeErrorCode, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
        {
            Exhausted.Add((id, leaseToken, safeErrorCode, completedAtUtc));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMediaStorage(TourMediaStorageDeleteResult deleteResult) : ITourMediaStorage
    {
        public string? LastPublicId { get; private set; }
        public string AllocatePublicId(long tourId) => throw new NotSupportedException();
        public Task<TourMediaStorageUploadResult> UploadAsync(TourMediaStorageUpload request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TourMediaStorageDeleteResult> DestroyAsync(string publicId, CancellationToken cancellationToken)
        {
            LastPublicId = publicId;
            return Task.FromResult(deleteResult);
        }
    }

    private sealed class SequencedCleanupStore(Queue<TourMediaCleanupClaim> claims) : ITourMediaCleanupOutboxStore
    {
        public int ClaimCalls { get; private set; }
        public List<(long Id, Guid Token, DateTimeOffset At)> Completed { get; } = [];

        public Task<IReadOnlyList<TourMediaCleanupClaim>> ClaimDueAsync(
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int batchSize,
            CancellationToken cancellationToken)
        {
            batchSize.Should().Be(1);
            ClaimCalls++;
            IReadOnlyList<TourMediaCleanupClaim> result = claims.TryDequeue(out TourMediaCleanupClaim? claim)
                ? [claim]
                : [];
            return Task.FromResult(result);
        }

        public Task CompleteAsync(long id, Guid leaseToken, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
        {
            Completed.Add((id, leaseToken, completedAtUtc));
            return Task.CompletedTask;
        }

        public Task RetryAsync(
            long id,
            Guid leaseToken,
            string safeErrorCode,
            DateTimeOffset notBeforeUtc,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExhaustAsync(
            long id,
            Guid leaseToken,
            string safeErrorCode,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BlockingMediaStorage : ITourMediaStorage
    {
        private int destroyCalls;

        public TaskCompletionSource<bool> FirstDestroyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DestroyCalls => Volatile.Read(ref destroyCalls);

        public string AllocatePublicId(long tourId) => throw new NotSupportedException();

        public Task<TourMediaStorageUploadResult> UploadAsync(
            TourMediaStorageUpload request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<TourMediaStorageDeleteResult> DestroyAsync(
            string publicId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref destroyCalls) == 1)
            {
                FirstDestroyStarted.TrySetResult(true);
                await ReleaseFirstDestroySource.Task.WaitAsync(cancellationToken);
            }

            return new TourMediaStorageDeleteResult(TourMediaStorageDeleteOutcome.Deleted, null);
        }

        private TaskCompletionSource<bool> ReleaseFirstDestroySource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFirstDestroy() => ReleaseFirstDestroySource.TrySetResult(true);
    }
}