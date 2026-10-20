using FluentAssertions;

using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.GetHistory;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.Revenue.Exports.GetHistory;

public sealed class GetRevenueExportHistoryQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_ReturnsOnlyTheCallersOwnExportsNewestFirstWithADownloadUrlOnlyWhenReady()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var otherOperator = await SeedApprovedOperatorAsync(db, 8);

        var older = MakeExport(operatorUser.Id, requestedAt: Now.AddDays(-2));
        var ready = MakeExport(operatorUser.Id, requestedAt: Now.AddDays(-1));
        ready.MarkReady("file.xlsx", $"tripmate/revenue-exports/{operatorUser.Id}/2-opaque", 1, Now.AddDays(7), Now);
        var foreign = MakeExport(otherOperator.Id, requestedAt: Now);

        db.RevenueExports.AddRange(older, ready, foreign);
        await db.SaveChangesAsync();

        var handler = new GetRevenueExportHistoryQueryHandler(
            db,
            new FakeCurrentUserService { UserId = operatorUser.Id, Role = nameof(UserRole.TourOperator) },
            new FakeDateTimeProvider { UtcNow = Now },
            new FakeRevenueExportStorage());

        var result = await handler.Handle(new GetRevenueExportHistoryQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2, "the foreign operator's export must never be included");
        result.Value.Items.Select(i => i.Id).Should().Equal(ready.Id, older.Id);
        result.Value.Items.First(i => i.Id == ready.Id).DownloadUrl.Should().NotBeNullOrEmpty();
        result.Value.Items.First(i => i.Id == older.Id).DownloadUrl.Should().BeNull();
    }

    private static RevenueExport MakeExport(long operatorUserId, DateTimeOffset requestedAt) =>
        RevenueExport.Create(
            operatorUserId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "monthly", null,
            RevenueExportFormat.Xlsx, RevenueExportScope.Summary, requestedAt);

    private sealed class FakeRevenueExportStorage : IRevenueExportStorage
    {
        public string AllocatePublicId(long operatorUserId, long exportId) => $"fake/{operatorUserId}/{exportId}";

        public Task<RevenueExportStorageUploadResult> UploadAsync(
            RevenueExportStorageUpload upload, CancellationToken cancellationToken) =>
            Task.FromResult(RevenueExportStorageUploadResult.Succeeded(upload.PublicId));

        public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Uri? CreateTemporaryDownloadUrl(string storageKey, DateTimeOffset expiresAtUtc) =>
            new($"https://fake-storage.test/{storageKey}?expires={expiresAtUtc:O}");
    }
}
