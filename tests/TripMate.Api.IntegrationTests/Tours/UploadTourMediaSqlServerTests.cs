using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.TourMedia.Upload;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Tours;

/// <summary>
/// Exercises the application-lock path against real SQL Server. These tests are
/// intentionally not HTTP tests: TM-207 Task 5 owns the command workflow, while
/// the HTTP surface is introduced by a later task.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class UploadTourMediaSqlServerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentSameKey_CreatesOneOperationOneMediaAndOneProviderAsset()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var storage = new ConcurrentStorage();
        var idempotencyKey = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<long> UploadAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            var result = await CreateHandler(context, storage).Handle(
                CreateCommand(seed.TourId, seed.ActorUserId, idempotencyKey),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value.TourMediaId;
        }

        var first = UploadAsync();
        var second = UploadAsync();
        gate.SetResult();
        var mediaIds = await Task.WhenAll(first, second);

        mediaIds[0].Should().Be(mediaIds[1]);
        await using var verification = database.CreateDbContext();
        (await verification.TourMedia.CountAsync()).Should().Be(1);
        (await verification.TourMediaUploadOperations.CountAsync()).Should().Be(1);
        storage.ActiveAssetCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentDifferentKeys_AtCapacityLeavesAtMostTenActiveImages()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, activeImageCount: 9);
        var storage = new ConcurrentStorage();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> UploadAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            var result = await CreateHandler(context, storage).Handle(
                CreateCommand(seed.TourId, seed.ActorUserId, Guid.NewGuid()),
                CancellationToken.None);
            return result.IsSuccess;
        }

        var first = UploadAsync();
        var second = UploadAsync();
        gate.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        outcomes.Count(outcome => outcome).Should().Be(1);
        await using var verification = database.CreateDbContext();
        (await verification.TourMedia.CountAsync(media =>
            media.TourId == seed.TourId &&
            media.LifecycleStatus == TourMediaLifecycleStatus.Active)).Should().Be(10);
        storage.ActiveAssetCount.Should().Be(1);
    }

    private static UploadTourMediaCommandHandler CreateHandler(
        ApplicationDbContext context,
        ITourMediaStorage storage) =>
        new(
            context,
            new FixedDateTimeProvider(),
            new AcceptedImageInspector(),
            storage,
            new SqlServerTourMediaUploadLock(context));

    private static UploadTourMediaCommand CreateCommand(
        long tourId,
        long actorUserId,
        Guid idempotencyKey) =>
        new(
            tourId,
            actorUserId,
            idempotencyKey,
            new TourMediaImageSource(new MemoryStream([1, 2, 3]), 3, "tour.png", "image/png"),
            "Tour image",
            "Accessible tour image",
            false);

    private static async Task<(long ActorUserId, long TourId)> SeedAsync(
        SqlServerTestDatabase database,
        int activeImageCount = 0)
    {
        await using var context = database.CreateDbContext();
        var actor = new User
        {
            Email = $"media-{Guid.NewGuid():N}@example.com",
            FullName = "Approved operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        context.Users.Add(actor);
        await context.SaveChangesAsync();

        context.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = actor.Id,
            CompanyName = "TripMate Test Operator",
            TaxCode = $"TAX{actor.Id}{Guid.NewGuid():N}",
            BusinessLicenseNo = $"LIC{actor.Id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });
        var tour = CreateTour(actor.Id);
        context.Tours.Add(tour);
        await context.SaveChangesAsync();

        for (var index = 1; index <= activeImageCount; index++)
        {
            context.TourMedia.Add(TripMate.Domain.Entities.TourMedia.Create(
                tour,
                $"tripmate/tours/{tour.Id}/existing-{index}",
                $"https://res.cloudinary.com/tripmate/image/upload/existing-{index}.webp",
                null,
                "Existing image",
                index,
                false,
                Now));
        }

        await context.SaveChangesAsync();
        return (actor.Id, tour.Id);
    }

    private static Tour CreateTour(long operatorUserId)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        SetPrivateProperty(tour, nameof(Tour.OperatorUserId), operatorUserId);
        SetPrivateProperty(tour, nameof(Tour.Title), "Concurrency tour");
        SetPrivateProperty(tour, nameof(Tour.BasePrice), 1_000_000m);
        SetPrivateProperty(tour, nameof(Tour.DurationDays), 1);
        SetPrivateProperty(tour, nameof(Tour.Status), TourStatus.Draft);
        SetPrivateProperty(tour, nameof(Tour.CreatedAtUtc), Now);
        SetPrivateProperty(tour, nameof(Tour.UpdatedAtUtc), Now);
        return tour;
    }

    private static void SetPrivateProperty<T>(object instance, string propertyName, T value) =>
        instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class AcceptedImageInspector : ITourMediaImageInspector
    {
        public Task<TourMediaImageInspectionResult> InspectAsync(
            TourMediaImageSource source,
            CancellationToken cancellationToken) =>
            Task.FromResult(TourMediaImageInspectionResult.Accepted(new(
                [1, 2, 3], "image/png", ".png", 3, 2,
                "A7D4B398C2B7B56F18AB501ED864E3B40D6C0F5B12A812CE1FC1ACBDBB9B67D0")));
    }

    private sealed class ConcurrentStorage : ITourMediaStorage
    {
        private long _nextId;
        private readonly HashSet<string> _activeAssets = [];
        private readonly Lock _gate = new();

        public int ActiveAssetCount
        {
            get
            {
                lock (_gate)
                {
                    return _activeAssets.Count;
                }
            }
        }

        public string AllocatePublicId(long tourId) =>
            $"tripmate/tours/{tourId}/{Interlocked.Increment(ref _nextId):D32}";

        public async Task<TourMediaStorageUploadResult> UploadAsync(
            TourMediaStorageUpload request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(25, cancellationToken);
            lock (_gate)
            {
                _activeAssets.Add(request.PublicId);
            }

            return TourMediaStorageUploadResult.Succeeded(new Uri(
                $"https://res.cloudinary.com/tripmate/image/upload/{request.PublicId}.webp"));
        }

        public Task<TourMediaStorageDeleteResult> DestroyAsync(
            string publicId,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _activeAssets.Remove(publicId);
            }

            return Task.FromResult(new TourMediaStorageDeleteResult(
                TourMediaStorageDeleteOutcome.Deleted,
                null));
        }
    }
}