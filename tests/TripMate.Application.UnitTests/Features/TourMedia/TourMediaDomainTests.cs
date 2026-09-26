using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TourMedia;

public sealed class TourMediaDomainTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_AllowsZeroOrOnePrimaryAndNormalizesMetadata()
    {
        var tour = CreateTour(42);

        var regular = global::TripMate.Domain.Entities.TourMedia.Create(
            tour,
            "  tripmate/tours/42/regular  ",
            " https://res.cloudinary.com/tripmate/image/upload/regular.webp ",
            "  Riverside view  ",
            "  Tour group beside the river  ",
            sortOrder: 1,
            isPrimary: false,
            Now);
        var primary = global::TripMate.Domain.Entities.TourMedia.Create(
            tour,
            "tripmate/tours/42/primary",
            "https://res.cloudinary.com/tripmate/image/upload/primary.webp",
            caption: null,
            altText: "Primary tour image",
            sortOrder: 2,
            isPrimary: true,
            Now);

        regular.Tour.Should().BeSameAs(tour);
        regular.TourId.Should().Be(42);
        regular.CloudinaryPublicId.Should().Be("tripmate/tours/42/regular");
        regular.DeliveryUrl.Should().StartWith("https://");
        regular.Caption.Should().Be("Riverside view");
        regular.AltText.Should().Be("Tour group beside the river");
        regular.LifecycleStatus.Should().Be(TourMediaLifecycleStatus.Active);
        regular.IsPrimary.Should().BeFalse();
        regular.DeletedAtUtc.Should().BeNull();
        primary.IsPrimary.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_RejectsBlankAltText(string altText)
    {
        var action = () => global::TripMate.Domain.Entities.TourMedia.Create(
            CreateTour(42),
            "tripmate/tours/42/image",
            "https://res.cloudinary.com/tripmate/image/upload/image.webp",
            caption: null,
            altText,
            sortOrder: 1,
            isPrimary: false,
            Now);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MetadataOrderAndSoftDelete_EnforceMutationBoundaries()
    {
        var media = CreateMedia();

        media.UpdateMetadata("  Updated caption  ", "  Updated alt  ", Now.AddMinutes(1));
        media.SetOrderAndPrimary(3, isPrimary: true, Now.AddMinutes(2));
        media.SoftDelete(Now.AddMinutes(3));

        media.Caption.Should().Be("Updated caption");
        media.AltText.Should().Be("Updated alt");
        media.SortOrder.Should().Be(3);
        media.IsPrimary.Should().BeFalse("a deleted image cannot remain primary");
        media.LifecycleStatus.Should().Be(TourMediaLifecycleStatus.Deleted);
        media.DeletedAtUtc.Should().Be(Now.AddMinutes(3));

        var editDeleted = () => media.UpdateMetadata("caption", "alt", Now.AddMinutes(4));
        var reorderDeleted = () => media.SetOrderAndPrimary(1, false, Now.AddMinutes(4));
        editDeleted.Should().Throw<InvalidOperationException>();
        reorderDeleted.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void UploadOperation_RequiresPendingUploadedCompletedTransition()
    {
        var actor = new User { Id = 7 };
        var tour = CreateTour(42);
        var operation = TourMediaUploadOperation.Create(
            actor,
            tour,
            Guid.Parse("8a7d412d-af20-4bee-8d28-d135d039ed65"),
            new string('A', TourMediaUploadOperation.PayloadFingerprintLength),
            "tripmate/tours/42/operation-image",
            Now);

        operation.Status.Should().Be(TourMediaUploadOperationStatus.Pending);
        operation.ActorUser.Should().BeSameAs(actor);
        operation.Tour.Should().BeSameAs(tour);

        operation.MarkProviderUploaded(Now.AddMinutes(1));
        operation.Status.Should().Be(TourMediaUploadOperationStatus.Uploaded);
        operation.ProviderUploadedAtUtc.Should().Be(Now.AddMinutes(1));

        operation.Complete(tourMediaId: 99, Now.AddMinutes(2));
        operation.Status.Should().Be(TourMediaUploadOperationStatus.Completed);
        operation.TourMediaId.Should().Be(99);
        operation.CompletedAtUtc.Should().Be(Now.AddMinutes(2));

        var repeat = () => operation.MarkProviderUploaded(Now.AddMinutes(3));
        repeat.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CleanupOutbox_RetriesThenExhaustsAtConfiguredLimit()
    {
        var media = CreateMedia();
        media.SoftDelete(Now);
        var item = TourMediaCleanupOutboxItem.Create(
            media,
            notBeforeAtUtc: Now.AddDays(30),
            createdAtUtc: Now,
            maxAttempts: 2);

        item.BeginAttempt(
            Guid.Parse("4d71ca25-d953-4c17-920e-71636fe4e8f9"),
            leaseExpiresAtUtc: Now.AddDays(30).AddMinutes(5),
            startedAtUtc: Now.AddDays(30));
        item.ScheduleRetry("PROVIDER_UNAVAILABLE", Now.AddDays(30).AddHours(1), Now.AddDays(30));
        item.Status.Should().Be(TourMediaCleanupStatus.Pending);
        item.AttemptCount.Should().Be(1);
        item.LeaseToken.Should().BeNull();

        item.BeginAttempt(
            Guid.Parse("fe94c431-10b7-40dd-a71f-e0aef84fed51"),
            leaseExpiresAtUtc: Now.AddDays(30).AddHours(1).AddMinutes(5),
            startedAtUtc: Now.AddDays(30).AddHours(1));
        item.Exhaust("PROVIDER_REJECTED", Now.AddDays(30).AddHours(1));

        item.Status.Should().Be(TourMediaCleanupStatus.Exhausted);
        item.AttemptCount.Should().Be(2);
        item.CompletedAtUtc.Should().NotBeNull();
        item.LeaseToken.Should().BeNull();
    }

    [Fact]
    public void CleanupOutbox_CompleteRequiresAnInProgressAttempt()
    {
        var media = CreateMedia();
        media.SoftDelete(Now);
        var item = TourMediaCleanupOutboxItem.Create(
            media,
            Now.AddDays(30),
            Now);

        var completePending = () => item.Complete(Now.AddDays(30));
        completePending.Should().Throw<InvalidOperationException>();

        item.BeginAttempt(Guid.NewGuid(), Now.AddDays(30).AddMinutes(5), Now.AddDays(30));
        item.Complete(Now.AddDays(30).AddMinutes(1));

        item.Status.Should().Be(TourMediaCleanupStatus.Completed);
        item.CompletedAtUtc.Should().Be(Now.AddDays(30).AddMinutes(1));
    }

    private static global::TripMate.Domain.Entities.TourMedia CreateMedia() =>
        global::TripMate.Domain.Entities.TourMedia.Create(
            CreateTour(42),
            "tripmate/tours/42/image",
            "https://res.cloudinary.com/tripmate/image/upload/image.webp",
            caption: null,
            altText: "Tour image",
            sortOrder: 1,
            isPrimary: false,
            Now);

    private static Tour CreateTour(long id)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        tour.Id = id;
        return tour;
    }
}