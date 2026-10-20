using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Worker;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.Revenue.Exports.Worker;

public sealed class RevenueExportWorkerProcessorTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);
    private static readonly DateOnly End = new(2026, 9, 30);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProcessQueuedBatchAsync_ClaimsRendersAndMarksReady()
    {
        var dbName = Guid.NewGuid().ToString();
        var exportId = await SeedQueuedExportWithOneBookingAsync(dbName);

        await using var db = TestDbContext.Create(dbName);
        var storage = new RecordingRevenueExportStorage();
        var processed = await CreateProcessor(db, storage).ProcessQueuedBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        storage.UploadCallCount.Should().Be(1);
        var export = await db.RevenueExports.SingleAsync(x => x.Id == exportId);
        export.Status.Should().Be(RevenueExportStatus.Ready);
        export.StorageKey.Should().NotBeNullOrEmpty();
        export.FileName.Should().EndWith(".xlsx");
    }

    [Fact]
    public async Task ProcessQueuedBatchAsync_StorageFails_MarksFailedWithoutAPartialAsset()
    {
        var dbName = Guid.NewGuid().ToString();
        var exportId = await SeedQueuedExportWithOneBookingAsync(dbName);

        await using var db = TestDbContext.Create(dbName);
        var storage = new RecordingRevenueExportStorage { FailUpload = true };
        var processed = await CreateProcessor(db, storage).ProcessQueuedBatchAsync(CancellationToken.None);

        processed.Should().Be(1, "the record was claimed and attempted even though rendering ultimately failed");
        var export = await db.RevenueExports.SingleAsync(x => x.Id == exportId);
        export.Status.Should().Be(RevenueExportStatus.Failed);
        export.StorageKey.Should().BeNull();
    }

    [Fact]
    public async Task ProcessQueuedBatchAsync_TwoWorkersRaceTheSameRecord_OnlyOneClaimsIt()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedQueuedExportWithOneBookingAsync(dbName);

        await using var db1 = TestDbContext.Create(dbName);
        await using var db2 = TestDbContext.Create(dbName);

        // Both workers read the still-Queued row before either commits a claim, simulating a
        // true race rather than a sequential handoff.
        await db1.RevenueExports.FirstAsync();
        await db2.RevenueExports.FirstAsync();

        var storage = new RecordingRevenueExportStorage();
        var processor1 = CreateProcessor(db1, storage);
        var processor2 = CreateProcessor(db2, storage);

        var processedBy1 = await processor1.ProcessQueuedBatchAsync(CancellationToken.None);
        var processedBy2 = await processor2.ProcessQueuedBatchAsync(CancellationToken.None);

        processedBy1.Should().Be(1);
        processedBy2.Should().Be(0, "the row_version concurrency token must reject the losing worker's claim");
        storage.UploadCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessQueuedBatchAsync_ReclaimsRecordStuckInProcessingPastStaleTimeout()
    {
        var dbName = Guid.NewGuid().ToString();
        var exportId = await SeedQueuedExportWithOneBookingAsync(dbName);

        // Simulate a worker that claimed the record and then crashed, was redeployed, or was
        // cancelled before reaching MarkReady/MarkFailed: claim it directly and leave it
        // Processing, with ProcessingStartedAtUtc far enough in the past to be stale.
        var claimedAt = Now - TimeSpan.FromMinutes(30);
        await using (var crashedDb = TestDbContext.Create(dbName))
        {
            var export = await crashedDb.RevenueExports.SingleAsync(x => x.Id == exportId);
            export.ClaimForProcessing(claimedAt);
            await crashedDb.SaveChangesAsync();
        }

        await using var db = TestDbContext.Create(dbName);
        var storage = new RecordingRevenueExportStorage();
        var processed = await CreateProcessor(
                db, storage, new RevenueExportOptions { StaleProcessingTimeoutMinutes = 15 })
            .ProcessQueuedBatchAsync(CancellationToken.None);

        processed.Should().Be(1, "the stale Processing record is reclaimed to Queued and processed in the same sweep");
        var reclaimed = await db.RevenueExports.SingleAsync(x => x.Id == exportId);
        reclaimed.Status.Should().Be(RevenueExportStatus.Ready);
        reclaimed.AttemptCount.Should().Be(2, "one attempt before the crash, one more after reclaim");
    }

    [Fact]
    public async Task ProcessQueuedBatchAsync_RecordStuckPastMaxAttempts_GivesUpAsFailedInsteadOfRetryingForever()
    {
        var dbName = Guid.NewGuid().ToString();
        var exportId = await SeedQueuedExportWithOneBookingAsync(dbName);

        var staleTimeout = TimeSpan.FromMinutes(15);
        await using (var crashedDb = TestDbContext.Create(dbName))
        {
            var crashedExport = await crashedDb.RevenueExports.SingleAsync(x => x.Id == exportId);
            crashedExport.ClaimForProcessing(Now - TimeSpan.FromMinutes(45)); // attempt 1, then "crashes"
            crashedExport.ReclaimIfStale(Now - TimeSpan.FromMinutes(30), staleTimeout, maxAttempts: 2); // -> Queued
            crashedExport.ClaimForProcessing(Now - TimeSpan.FromMinutes(30)); // attempt 2, then "crashes" again
            await crashedDb.SaveChangesAsync();
        }

        await using var db = TestDbContext.Create(dbName);
        var storage = new RecordingRevenueExportStorage();
        var processed = await CreateProcessor(
                db, storage, new RevenueExportOptions { StaleProcessingTimeoutMinutes = 15, MaxProcessingAttempts = 2 })
            .ProcessQueuedBatchAsync(CancellationToken.None);

        processed.Should().Be(0, "the record is given up as Failed, not reclaimed for a third attempt");
        var export = await db.RevenueExports.SingleAsync(x => x.Id == exportId);
        export.Status.Should().Be(RevenueExportStatus.Failed);
        export.FailureCode.Should().Be("stale_processing_timeout");
        storage.UploadCallCount.Should().Be(0);
    }

    private static async Task<long> SeedQueuedExportWithOneBookingAsync(string dbName)
    {
        await using var seedDb = TestDbContext.Create(dbName);
        var operatorUser = await SeedApprovedOperatorAsync(seedDb, 7);
        var tour = CreateTour(101, operatorUser.Id, "Tour");
        seedDb.Add(tour);
        await seedDb.SaveChangesAsync();
        var schedule = CreateTourSchedule(201, tour.Id);
        seedDb.Add(schedule);
        await seedDb.SaveChangesAsync();
        await SeedPaidBookingAsync(
            seedDb, 301, schedule.Id, 1_000_000m, new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));

        var export = RevenueExport.Create(
            operatorUser.Id, Start, End, "monthly", null,
            RevenueExportFormat.Xlsx, RevenueExportScope.Summary, Now);
        seedDb.RevenueExports.Add(export);
        await seedDb.SaveChangesAsync();
        return export.Id;
    }

    private static RevenueExportWorkerProcessor CreateProcessor(
        TestDbContext db, IRevenueExportStorage storage, RevenueExportOptions? options = null) =>
        new(
            db,
            new FakeDateTimeProvider { UtcNow = Now },
            new OperatorRevenueAggregator(db),
            new FakeXlsxRenderer(),
            storage,
            options ?? new RevenueExportOptions());

    private sealed class FakeXlsxRenderer : IRevenueExportRenderer
    {
        public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        public string FileExtension => "xlsx";
        public byte[] Render(RevenueExportDocument document) => [0x50, 0x4b, 0x03, 0x04];
    }

    private sealed class RecordingRevenueExportStorage : IRevenueExportStorage
    {
        public int UploadCallCount { get; private set; }
        public bool FailUpload { get; set; }

        public string AllocatePublicId(long operatorUserId, long exportId) =>
            $"tripmate/revenue-exports/{operatorUserId}/{exportId}-fake";

        public Task<RevenueExportStorageUploadResult> UploadAsync(
            RevenueExportStorageUpload upload, CancellationToken cancellationToken)
        {
            UploadCallCount++;
            return Task.FromResult(FailUpload
                ? RevenueExportStorageUploadResult.Failed("fake_storage_unavailable")
                : RevenueExportStorageUploadResult.Succeeded(upload.PublicId));
        }

        public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken) => Task.FromResult(true);

        public Uri? CreateTemporaryDownloadUrl(string storageKey, DateTimeOffset expiresAtUtc) =>
            new($"https://fake-storage.test/{storageKey}?expires={expiresAtUtc:O}");
    }
}
