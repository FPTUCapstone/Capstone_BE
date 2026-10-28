using FluentAssertions;

using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Download;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.Revenue.Exports.Download;

public sealed class GetRevenueExportDownloadQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_OwnedReadyUnexpiredExport_ReturnsAShortLivedSignedUrl()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var export = CreateReadyExport(operatorUser.Id);
        db.RevenueExports.Add(export);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db, operatorUser.Id);

        var result = await handler.Handle(new GetRevenueExportDownloadQuery(export.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FileName.Should().Be(export.FileName);
        result.Value.DownloadUrl.AbsoluteUri.Should().Contain(export.StorageKey);
    }

    [Fact]
    public async Task Handle_ForeignOperatorsExport_ReturnsNotFoundIndistinguishableFromMissing()
    {
        await using var db = TestDbContext.Create();
        var owner = await SeedApprovedOperatorAsync(db, 7);
        var otherOperator = await SeedApprovedOperatorAsync(db, 8);
        var export = CreateReadyExport(owner.Id);
        db.RevenueExports.Add(export);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db, otherOperator.Id);

        var result = await handler.Handle(new GetRevenueExportDownloadQuery(export.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(RevenueExportErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_MissingExportId_ReturnsTheSameNotFoundCodeAsAForeignExport()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var handler = CreateHandler(db, operatorUser.Id);

        var result = await handler.Handle(new GetRevenueExportDownloadQuery(999), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(RevenueExportErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_ExpiredExport_ReturnsNotFoundEvenThoughStatusIsStillReady()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var export = CreateReadyExport(operatorUser.Id, expiresAtUtc: Now.AddDays(-1));
        db.RevenueExports.Add(export);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db, operatorUser.Id);

        var result = await handler.Handle(new GetRevenueExportDownloadQuery(export.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(RevenueExportErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_FailedExport_ReturnsNotFoundAndNeverOffersAPartialFile()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var export = RevenueExport.Create(
            operatorUser.Id, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "monthly", null,
            RevenueExportFormat.Xlsx, RevenueExportScope.Summary, Now);
        export.MarkFailed("render_error", Now);
        db.RevenueExports.Add(export);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db, operatorUser.Id);

        var result = await handler.Handle(new GetRevenueExportDownloadQuery(export.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(RevenueExportErrorCodes.NotFound);
    }

    private static RevenueExport CreateReadyExport(long operatorUserId, DateTimeOffset? expiresAtUtc = null)
    {
        var export = RevenueExport.Create(
            operatorUserId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "monthly", null,
            RevenueExportFormat.Xlsx, RevenueExportScope.Summary, Now);
        export.MarkReady(
            "tripmate-revenue-20260901-20260930-1.xlsx",
            $"tripmate/revenue-exports/{operatorUserId}/1-opaque",
            rowCount: 3,
            expiresAtUtc: expiresAtUtc ?? Now.AddDays(7),
            nowUtc: Now);
        return export;
    }

    private static GetRevenueExportDownloadQueryHandler CreateHandler(TestDbContext db, long operatorUserId) =>
        new(
            db,
            new FakeCurrentUserService { UserId = operatorUserId, Role = nameof(UserRole.TourOperator) },
            new FakeDateTimeProvider { UtcNow = Now },
            new FakeRevenueExportStorage());

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