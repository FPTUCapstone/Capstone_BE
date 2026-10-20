using FluentAssertions;

using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.Revenue.Exports.Create;

public sealed class CreateRevenueExportCommandHandlerTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);
    private static readonly DateOnly End = new(2026, 9, 30);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_ValidRequestUnderThreshold_GeneratesSynchronouslyAndReturnsReady()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var tour = CreateTour(101, operatorUser.Id, "Ba Na Hills Full-Day Tour");
        db.Add(tour);
        await db.SaveChangesAsync();
        var schedule = CreateTourSchedule(201, tour.Id);
        db.Add(schedule);
        await db.SaveChangesAsync();
        await SeedPaidBookingAsync(db, 301, schedule.Id, 1_000_000m,
            new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));

        var storage = new RecordingRevenueExportStorage();
        var handler = CreateHandler(db, operatorUser.Id, storage);

        var result = await handler.Handle(
            new CreateRevenueExportCommand(Start, End, "monthly", null, "xlsx", "summary"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(RevenueExportStatus.Ready));
        result.Value.FileName.Should().EndWith(".xlsx");
        result.Value.DownloadUrl.Should().NotBeNullOrEmpty();
        storage.UploadCallCount.Should().Be(1);

        var persisted = db.RevenueExports.Single();
        persisted.Status.Should().Be(RevenueExportStatus.Ready);
        persisted.StorageKey.Should().NotBeNullOrEmpty();

        db.AuditLogs.Should().ContainSingle(a => a.ActionType == "REVENUE_EXPORT_REQUESTED");
    }

    [Fact]
    public async Task Handle_NoMatchingRecords_ReturnsMsg128AndCreatesNoExportRecord()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var handler = CreateHandler(db, operatorUser.Id, new RecordingRevenueExportStorage());

        var result = await handler.Handle(
            new CreateRevenueExportCommand(Start, End, "monthly", null, "xlsx", "summary"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(RevenueExportErrorCodes.NoRecords);
        db.RevenueExports.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_UnsupportedFormat_ReturnsMsg01WithoutTouchingTheDatabase()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var handler = CreateHandler(db, operatorUser.Id, new RecordingRevenueExportStorage());

        var result = await handler.Handle(
            new CreateRevenueExportCommand(Start, End, "monthly", null, "pdf", "summary"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(RevenueExportErrorCodes.ValidationFailed);
        db.RevenueExports.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_InvalidScope_ReturnsMsg01()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var handler = CreateHandler(db, operatorUser.Id, new RecordingRevenueExportStorage());

        var result = await handler.Handle(
            new CreateRevenueExportCommand(Start, End, "monthly", null, "xlsx", "everything"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(RevenueExportErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Handle_ForeignTourFilter_ReturnsMsg126IndistinguishableFromMissing()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var foreignOperator = await SeedApprovedOperatorAsync(db, 8);
        var foreignTour = CreateTour(500, foreignOperator.Id, "Foreign Tour");
        db.Add(foreignTour);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db, operatorUser.Id, new RecordingRevenueExportStorage());

        var result = await handler.Handle(
            new CreateRevenueExportCommand(Start, End, "monthly", 500, "xlsx", "summary"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(RevenueExportErrorCodes.AccessRequired);
    }

    [Fact]
    public async Task Handle_StorageUploadFails_MarksExportFailedAndReturnsMsg117WithoutPartialFile()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var tour = CreateTour(101, operatorUser.Id, "Tour");
        db.Add(tour);
        await db.SaveChangesAsync();
        var schedule = CreateTourSchedule(201, tour.Id);
        db.Add(schedule);
        await db.SaveChangesAsync();
        await SeedPaidBookingAsync(db, 301, schedule.Id, 1_000_000m,
            new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));

        var storage = new RecordingRevenueExportStorage { FailUpload = true };
        var handler = CreateHandler(db, operatorUser.Id, storage);

        var result = await handler.Handle(
            new CreateRevenueExportCommand(Start, End, "monthly", null, "xlsx", "summary"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(RevenueExportErrorCodes.GenerationFailed);

        var persisted = db.RevenueExports.Single();
        persisted.Status.Should().Be(RevenueExportStatus.Failed);
        persisted.StorageKey.Should().BeNull("a failed upload must never leave a partial file reference");
    }

    private static CreateRevenueExportCommandHandler CreateHandler(
        TestDbContext db, long operatorUserId, IRevenueExportStorage storage) =>
        new(
            db,
            new FakeCurrentUserService { UserId = operatorUserId, Role = nameof(UserRole.TourOperator) },
            new FakeDateTimeProvider { UtcNow = Now },
            new OperatorRevenueAggregator(db),
            new FakeXlsxRenderer(),
            storage,
            new RevenueExportOptions());

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

        public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Uri? CreateTemporaryDownloadUrl(string storageKey, DateTimeOffset expiresAtUtc) =>
            new($"https://fake-storage.test/{storageKey}?expires={expiresAtUtc:O}");
    }
}