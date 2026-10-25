using FluentAssertions;

using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Worker;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.Revenue.Exports.Worker;

public sealed class RevenueExportExpiryProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExpireDueExportsAsync_DeletesTheAssetAndMarksExpired()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var export = MakeReadyExport(operatorUser.Id, expiresAtUtc: Now.AddMinutes(-1));
        db.RevenueExports.Add(export);
        await db.SaveChangesAsync();

        var storage = new RecordingRevenueExportStorage();
        var processor = new RevenueExportExpiryProcessor(db, new FakeDateTimeProvider { UtcNow = Now }, storage);

        var expiredCount = await processor.ExpireDueExportsAsync(CancellationToken.None);

        expiredCount.Should().Be(1);
        storage.DeletedKeys.Should().ContainSingle().Which.Should().Be(export.StorageKey);
        var reloaded = db.RevenueExports.Single();
        reloaded.Status.Should().Be(RevenueExportStatus.Expired);
    }

    [Fact]
    public async Task ExpireDueExportsAsync_LeavesNotYetDueExportsUntouched()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var export = MakeReadyExport(operatorUser.Id, expiresAtUtc: Now.AddDays(1));
        db.RevenueExports.Add(export);
        await db.SaveChangesAsync();

        var storage = new RecordingRevenueExportStorage();
        var processor = new RevenueExportExpiryProcessor(db, new FakeDateTimeProvider { UtcNow = Now }, storage);

        var expiredCount = await processor.ExpireDueExportsAsync(CancellationToken.None);

        expiredCount.Should().Be(0);
        storage.DeletedKeys.Should().BeEmpty();
        db.RevenueExports.Single().Status.Should().Be(RevenueExportStatus.Ready);
    }

    private static RevenueExport MakeReadyExport(long operatorUserId, DateTimeOffset expiresAtUtc)
    {
        var export = RevenueExport.Create(
            operatorUserId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "monthly", null,
            RevenueExportFormat.Xlsx, RevenueExportScope.Summary, Now);
        export.MarkReady(
            "file.xlsx", $"tripmate/revenue-exports/{operatorUserId}/1-opaque", 1, expiresAtUtc, Now);
        return export;
    }

    private sealed class RecordingRevenueExportStorage : IRevenueExportStorage
    {
        public List<string> DeletedKeys { get; } = [];

        public string AllocatePublicId(long operatorUserId, long exportId) => $"fake/{operatorUserId}/{exportId}";

        public Task<RevenueExportStorageUploadResult> UploadAsync(
            RevenueExportStorageUpload upload, CancellationToken cancellationToken) =>
            Task.FromResult(RevenueExportStorageUploadResult.Succeeded(upload.PublicId));

        public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(storageKey);
            return Task.FromResult(true);
        }

        public Uri? CreateTemporaryDownloadUrl(string storageKey, DateTimeOffset expiresAtUtc) => null;
    }
}