using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TourMedia.Common;
using TripMate.Application.Features.TourMedia.Delete;
using TripMate.Application.Features.TourMedia.List;
using TripMate.Application.Features.TourMedia.Reorder;
using TripMate.Application.Features.TourMedia.UpdateMetadata;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TourMedia.Manage;

public sealed class TourMediaManagementHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task List_OwnedTour_ReturnsOnlyActiveMediaInStableOrder()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var deleted = SeedMedia(db, tour, 3, false, "deleted");
        deleted.SoftDelete(Now);
        SeedMedia(db, tour, 2, false, "second");
        var first = SeedMedia(db, tour, 1, true, "first");
        await db.SaveChangesAsync();

        var result = await new ListTourMediaQueryHandler(db).Handle(
            new ListTourMediaQuery(tour.Id, actor.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(item => item.TourMediaId).Should().Equal(first.Id,
            db.TourMedia.Single(media => media.CloudinaryPublicId.EndsWith("second")).Id);
        result.Value.Should().OnlyContain(item => item.UpdatedAtUtc == Now);
    }

    [Fact]
    public async Task UpdateMetadata_ApprovedTour_AllowsAltTextOnlyAndRejectsCaptionChange()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Approved);
        var media = SeedMedia(db, tour, 1, true, "one", "Original caption");
        await db.SaveChangesAsync();
        var handler = new UpdateTourMediaMetadataCommandHandler(db, new FixedDateTimeProvider());

        var allowed = await handler.Handle(new UpdateTourMediaMetadataCommand(
            tour.Id, media.Id, actor.Id, "Original caption", " Corrected alt "), CancellationToken.None);
        var denied = await handler.Handle(new UpdateTourMediaMetadataCommand(
            tour.Id, media.Id, actor.Id, "Changed caption", "Corrected alt"), CancellationToken.None);

        allowed.IsSuccess.Should().BeTrue();
        allowed.Value.AltText.Should().Be("Corrected alt");
        denied.IsFailure.Should().BeTrue();
        denied.ErrorCode.Should().Be(TourMediaErrorCodes.TourMediaChangeLocked);
    }

    [Fact]
    public async Task UpdateMetadata_PendingTour_RejectsEvenAltTextOnlyCorrection()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Pending);
        var media = SeedMedia(db, tour, 1, true, "one", "Original caption");
        await db.SaveChangesAsync();

        var result = await new UpdateTourMediaMetadataCommandHandler(
            db, new FixedDateTimeProvider()).Handle(new UpdateTourMediaMetadataCommand(
                tour.Id, media.Id, actor.Id, "Original caption", "Corrected alt"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourMediaErrorCodes.TourMediaChangeLocked);
    }

    [Fact]
    public async Task UpdateMetadata_InvalidAltText_DoesNotLeaveCaptionMutationTracked()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var media = SeedMedia(db, tour, 1, true, "one", "Original caption");
        await db.SaveChangesAsync();

        var result = await new UpdateTourMediaMetadataCommandHandler(
            db, new FixedDateTimeProvider()).Handle(new UpdateTourMediaMetadataCommand(
                tour.Id, media.Id, actor.Id, "Changed caption", "   "), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourMediaErrorCodes.InvalidRequest);
        media.Caption.Should().Be("Original caption");
    }

    [Fact]
    public async Task Reorder_InvalidCompleteSet_DoesNotChangeAnyActiveOrder()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var first = SeedMedia(db, tour, 1, true, "one");
        var second = SeedMedia(db, tour, 2, false, "two");
        await db.SaveChangesAsync();

        var result = await new ReorderTourMediaCommandHandler(
            db, new FixedDateTimeProvider(), new NoopTourMediaLock()).Handle(
            new ReorderTourMediaCommand(tour.Id, actor.Id, [second.Id, second.Id], first.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourMediaErrorCodes.InvalidCompleteOrder);
        (await db.TourMedia.FindAsync(first.Id))!.SortOrder.Should().Be(1);
        (await db.TourMedia.FindAsync(second.Id))!.SortOrder.Should().Be(2);
    }

    [Fact]
    public async Task Reorder_CompleteSet_UsesContiguousOrderAndExactlyOneRequestedPrimary()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var first = SeedMedia(db, tour, 1, true, "one");
        var second = SeedMedia(db, tour, 2, false, "two");
        await db.SaveChangesAsync();

        var result = await new ReorderTourMediaCommandHandler(
            db, new FixedDateTimeProvider(), new NoopTourMediaLock()).Handle(
            new ReorderTourMediaCommand(tour.Id, actor.Id, [second.Id, first.Id], second.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(item => (item.TourMediaId, item.SortOrder, item.IsPrimary))
            .Should().Equal((second.Id, 1, true), (first.Id, 2, false));
    }

    [Fact]
    public async Task Reorder_WithoutPrimaryMediaId_PreservesExistingPrimaryAfterReload()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var first = SeedMedia(db, tour, 1, true, "one");
        var second = SeedMedia(db, tour, 2, false, "two");
        await db.SaveChangesAsync();

        var result = await new ReorderTourMediaCommandHandler(
            db, new FixedDateTimeProvider(), new NoopTourMediaLock()).Handle(
            new ReorderTourMediaCommand(tour.Id, actor.Id, [second.Id, first.Id], null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(item => (item.TourMediaId, item.SortOrder, item.IsPrimary))
            .Should().Equal((second.Id, 1, false), (first.Id, 2, true));
        db.ChangeTracker.Clear();
        (await db.TourMedia.FindAsync(first.Id))!.IsPrimary.Should().BeTrue();
        (await db.TourMedia.FindAsync(second.Id))!.IsPrimary.Should().BeFalse();
    }

    [Fact]
    public async Task Reorder_WithoutPrimaryMediaIdAndNoExistingPrimary_DoesNotPromoteAnImage()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var first = SeedMedia(db, tour, 1, false, "one");
        var second = SeedMedia(db, tour, 2, false, "two");
        await db.SaveChangesAsync();

        var result = await new ReorderTourMediaCommandHandler(
            db, new FixedDateTimeProvider(), new NoopTourMediaLock()).Handle(
            new ReorderTourMediaCommand(tour.Id, actor.Id, [second.Id, first.Id], null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().OnlyContain(item => !item.IsPrimary);
    }

    [Fact]
    public async Task Reorder_EmptyOwnedTour_AllowsZeroPrimaryAndRecordsAudit()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        await db.SaveChangesAsync();

        var result = await new ReorderTourMediaCommandHandler(
            db, new FixedDateTimeProvider(), new NoopTourMediaLock()).Handle(
            new ReorderTourMediaCommand(tour.Id, actor.Id, [], null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        (await db.AuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Delete_PrimaryMedia_SoftDeletesCompactsOrdersQueuesCleanupAndDoesNotPromote()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var primary = SeedMedia(db, tour, 1, true, "one");
        var remaining = SeedMedia(db, tour, 2, false, "two");
        await db.SaveChangesAsync();

        var result = await new DeleteTourMediaCommandHandler(
            db, new FixedDateTimeProvider(), new NoopTourMediaLock()).Handle(
            new DeleteTourMediaCommand(tour.Id, primary.Id, actor.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.TourMedia.FindAsync(primary.Id))!.LifecycleStatus.Should().Be(TourMediaLifecycleStatus.Deleted);
        (await db.TourMedia.FindAsync(primary.Id))!.IsPrimary.Should().BeFalse();
        (await db.TourMedia.FindAsync(remaining.Id))!.SortOrder.Should().Be(1);
        (await db.TourMedia.AnyAsync(media => media.TourId == tour.Id && media.IsPrimary)).Should().BeFalse();
        var cleanup = await db.TourMediaCleanupOutbox.SingleAsync();
        cleanup.NotBeforeAtUtc.Should().Be(Now.AddDays(30));
        (await db.AuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Delete_RepeatedOwnedDeletion_IsIdempotentAndDoesNotDuplicateCleanup()
    {
        await using var db = TestDbContext.Create();
        var actor = SeedOperator(db, 7);
        var tour = SeedTour(db, 42, actor.Id, TourStatus.Draft);
        var media = SeedMedia(db, tour, 1, false, "one");
        await db.SaveChangesAsync();
        var handler = new DeleteTourMediaCommandHandler(
            db, new FixedDateTimeProvider(), new NoopTourMediaLock());

        (await handler.Handle(new DeleteTourMediaCommand(tour.Id, media.Id, actor.Id), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        (await handler.Handle(new DeleteTourMediaCommand(tour.Id, media.Id, actor.Id), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        (await db.TourMediaCleanupOutbox.CountAsync()).Should().Be(1);
    }

    private static User SeedOperator(TestDbContext db, long id)
    {
        var user = new User { Id = id, Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = id,
            User = user,
            CompanyName = "Operator",
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
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Status), status);
        Set(tour, nameof(Tour.Title), "Test tour");
        db.Tours.Add(tour);
        return tour;
    }

    private static global::TripMate.Domain.Entities.TourMedia SeedMedia(
        TestDbContext db, Tour tour, int order, bool primary, string suffix, string? caption = null)
    {
        var media = global::TripMate.Domain.Entities.TourMedia.Create(
            tour,
            $"tripmate/tours/{tour.Id}/{suffix}",
            $"https://res.cloudinary.com/tripmate/image/upload/{suffix}.webp",
            caption,
            "Accessible image",
            order,
            primary,
            Now);
        db.TourMedia.Add(media);
        return media;
    }

    private static void Set<T>(object instance, string propertyName, T value) =>
        instance.GetType().GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class NoopTourMediaLock : ITourMediaUploadLock
    {
        public Task AcquireOperationAsync(long tourId, long actorUserId, Guid idempotencyKey,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireTourAsync(long tourId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}