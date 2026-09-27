using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.TourMedia.Common;
using TripMate.Application.Features.TourMedia.Upload;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TourMedia.Upload;

public sealed class UploadTourMediaCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_EligibleOwner_CreatesMediaOperationAndSafeAudit()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedEligibleOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var inspector = new RecordingInspector();
        var storage = new RecordingStorage();
        var handler = CreateHandler(db, inspector, storage);

        var result = await handler.Handle(CreateCommand(tour.Id, actor.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TourId.Should().Be(tour.Id);
        result.Value.SortOrder.Should().Be(1);
        result.Value.IsPrimary.Should().BeFalse();
        result.Value.DeliveryUrl.Should().StartWith("https://");
        (await db.TourMedia.SingleAsync()).AltText.Should().Be("Accessible tour image");
        (await db.TourMediaUploadOperations.SingleAsync()).Status
            .Should().Be(TourMediaUploadOperationStatus.Completed);
        (await db.AuditLogs.SingleAsync()).AfterData.Should().NotContain("cloudinary");
        storage.UploadPublicIds.Should().ContainSingle();
        storage.DestroyPublicIds.Should().BeEmpty();
        inspector.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_RepeatedCompletedKeyAndSameFingerprint_ReplaysWithoutProviderCall()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedEligibleOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var key = Guid.NewGuid();
        var command = CreateCommand(tour.Id, actor.Id, key);
        var firstStorage = new RecordingStorage();
        var firstHandler = CreateHandler(db, new RecordingInspector(), firstStorage);

        var first = await firstHandler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        var storage = new RecordingStorage();
        var handler = CreateHandler(db, new RecordingInspector(), storage);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TourMediaId.Should().Be(first.Value.TourMediaId);
        storage.UploadPublicIds.Should().BeEmpty();
        (await db.TourMedia.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_ReusedKeyWithDifferentFingerprint_ReturnsConflictWithoutProviderCall()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedEligibleOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var key = Guid.NewGuid();
        var operation = TourMediaUploadOperation.Create(
            actor,
            tour,
            key,
            new string('B', TourMediaUploadOperation.PayloadFingerprintLength),
            "tripmate/tours/42/existing",
            Now);
        db.TourMediaUploadOperations.Add(operation);
        await db.SaveChangesAsync(CancellationToken.None);
        var storage = new RecordingStorage();
        var handler = CreateHandler(db, new RecordingInspector(), storage);

        var result = await handler.Handle(CreateCommand(tour.Id, actor.Id, key), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourMediaErrorCodes.IdempotencyKeyPayloadMismatch);
        storage.UploadPublicIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_NonOwner_ReturnsNonDisclosingNotFoundWithoutProviderCall()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedEligibleOperator(db, 7);
        SeedEligibleOperator(db, 8);
        var tour = SeedTour(db, 42, operatorUserId: 8, TourStatus.Draft);
        var storage = new RecordingStorage();
        var handler = CreateHandler(db, new RecordingInspector(), storage);

        var result = await handler.Handle(CreateCommand(tour.Id, actor.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourMediaErrorCodes.TourNotFound);
        storage.UploadPublicIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(TourStatus.Pending)]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Inactive)]
    public async Task Handle_TourDoesNotAllowMaterialMediaChange_ReturnsConflict(TourStatus status)
    {
        await using var db = TestDbContext.Create();
        var actor = SeedEligibleOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, status);
        var storage = new RecordingStorage();
        var handler = CreateHandler(db, new RecordingInspector(), storage);

        var result = await handler.Handle(CreateCommand(tour.Id, actor.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourMediaErrorCodes.TourMediaChangeLocked);
        storage.UploadPublicIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_TenActiveImages_ReturnsLimitConflictWithoutProviderCall()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedEligibleOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        for (var index = 1; index <= 10; index++)
        {
            db.TourMedia.Add(global::TripMate.Domain.Entities.TourMedia.Create(
                tour,
                $"tripmate/tours/42/{index}",
                $"https://res.cloudinary.com/tripmate/image/upload/{index}.webp",
                null,
                "Accessible tour image",
                index,
                false,
                Now));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        var storage = new RecordingStorage();
        var handler = CreateHandler(db, new RecordingInspector(), storage);

        var result = await handler.Handle(CreateCommand(tour.Id, actor.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourMediaErrorCodes.ActiveImageLimitReached);
        storage.UploadPublicIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_RequestedPrimaryWhenPrimaryExists_ReturnsConflictWithoutProviderCall()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedEligibleOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        db.TourMedia.Add(global::TripMate.Domain.Entities.TourMedia.Create(
            tour,
            "tripmate/tours/42/primary",
            "https://res.cloudinary.com/tripmate/image/upload/primary.webp",
            null,
            "Existing primary",
            1,
            true,
            Now));
        await db.SaveChangesAsync(CancellationToken.None);
        var storage = new RecordingStorage();
        var handler = CreateHandler(db, new RecordingInspector(), storage);

        var result = await handler.Handle(
            CreateCommand(tour.Id, actor.Id) with { IsPrimary = true },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourMediaErrorCodes.ActivePrimaryAlreadyExists);
        storage.UploadPublicIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ProviderTransientFailure_ReturnsSafeErrorAndKeepsClaimForRetry()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedEligibleOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var storage = new RecordingStorage
        {
            UploadResult = TourMediaStorageUploadResult.Failed(
                TourMediaStorageFailureKind.Transient,
                "TOUR_MEDIA_STORAGE_UNAVAILABLE"),
        };
        var handler = CreateHandler(db, new RecordingInspector(), storage);

        var result = await handler.Handle(CreateCommand(tour.Id, actor.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourMediaErrorCodes.ProviderUnavailable);
        (await db.TourMedia.CountAsync()).Should().Be(0);
        (await db.TourMediaUploadOperations.SingleAsync()).Status
            .Should().Be(TourMediaUploadOperationStatus.Pending);
    }

    [Fact]
    public async Task Handle_CancellationFromInspector_PropagatesAndDoesNotClaimOperation()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedEligibleOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var handler = CreateHandler(db, new CancellingInspector(), new RecordingStorage());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var action = () => handler.Handle(CreateCommand(tour.Id, actor.Id), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        (await db.TourMediaUploadOperations.CountAsync()).Should().Be(0);
    }

    private static UploadTourMediaCommandHandler CreateHandler(
        TestDbContext db,
        ITourMediaImageInspector inspector,
        ITourMediaStorage storage)
    {
        db.SaveChanges();
        return new(
            db,
            new FakeDateTimeProvider { UtcNow = Now },
            inspector,
            storage,
            new RecordingUploadLock());
    }

    private static UploadTourMediaCommand CreateCommand(long tourId, long actorUserId, Guid? key = null) => new(
        tourId,
        actorUserId,
        key ?? Guid.NewGuid(),
        new TourMediaImageSource(
            new MemoryStream([1, 2, 3]),
            3,
            "tour.png",
            "image/png"),
        " Caption ",
        " Accessible tour image ",
        false);

    private static User SeedEligibleOperator(TestDbContext db, long id)
    {
        var user = new User
        {
            Id = id,
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            FullName = $"Operator {id}",
        };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = id,
            User = user,
            CompanyName = "TripMate Operator",
            TaxCode = $"TAX{id}",
            BusinessLicenseNo = $"LIC{id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        return user;
    }

    private static Tour SeedTour(TestDbContext db, long id, long operatorUserId, TourStatus status)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        tour.Id = id;
        SetPrivateProperty(tour, nameof(Tour.OperatorUserId), operatorUserId);
        SetPrivateProperty(tour, nameof(Tour.Status), status);
        SetPrivateProperty(tour, nameof(Tour.Title), "Test Tour");
        db.Tours.Add(tour);
        return tour;
    }

    private static void SetPrivateProperty<T>(object instance, string propertyName, T value) =>
        instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed class RecordingInspector : ITourMediaImageInspector
    {
        public const string Fingerprint = "A7D4B398C2B7B56F18AB501ED864E3B40D6C0F5B12A812CE1FC1ACBDBB9B67D0";

        public int CallCount { get; private set; }

        public Task<TourMediaImageInspectionResult> InspectAsync(
            TourMediaImageSource source,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(TourMediaImageInspectionResult.Accepted(new(
                [1, 2, 3],
                "image/png",
                ".png",
                3,
                2,
                Fingerprint)));
        }
    }

    private sealed class CancellingInspector : ITourMediaImageInspector
    {
        public Task<TourMediaImageInspectionResult> InspectAsync(
            TourMediaImageSource source,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<TourMediaImageInspectionResult>(cancellationToken);
    }

    private sealed class RecordingStorage : ITourMediaStorage
    {
        public List<string> UploadPublicIds { get; } = [];

        public List<string> DestroyPublicIds { get; } = [];

        public TourMediaStorageUploadResult UploadResult { get; set; } =
            TourMediaStorageUploadResult.Succeeded(
                new Uri("https://res.cloudinary.com/tripmate/image/upload/tour.png"));

        public string AllocatePublicId(long tourId) =>
            $"tripmate/tours/{tourId}/00000000000000000000000000000001";

        public Task<TourMediaStorageUploadResult> UploadAsync(
            TourMediaStorageUpload request,
            CancellationToken cancellationToken)
        {
            UploadPublicIds.Add(request.PublicId);
            return Task.FromResult(UploadResult);
        }

        public Task<TourMediaStorageDeleteResult> DestroyAsync(
            string publicId,
            CancellationToken cancellationToken)
        {
            DestroyPublicIds.Add(publicId);
            return Task.FromResult(new TourMediaStorageDeleteResult(
                TourMediaStorageDeleteOutcome.Deleted,
                null));
        }
    }

    private sealed class RecordingUploadLock : ITourMediaUploadLock
    {
        public Task AcquireOperationAsync(
            long tourId,
            long actorUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireTourAsync(long tourId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}