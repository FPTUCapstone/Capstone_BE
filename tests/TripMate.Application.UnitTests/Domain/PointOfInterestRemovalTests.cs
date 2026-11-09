using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class PointOfInterestRemovalTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 10, 1, 1, 30, 0, TimeSpan.Zero);

    [Fact]
    public void TryDeactivate_ActivePoi_TransitionsToInactiveAndPreservesExistingData()
    {
        var category = PoiCategory.Create("Museum", "Cultural collection");
        var tag = Tag.Create("History");
        var poi = PointOfInterest.Create(
            category,
            "Cham Museum",
            16.060537m,
            108.223203m,
            42,
            CreatedAtUtc,
            "02 2 Thang 9",
            "Museum of Cham sculpture",
            IndoorOutdoorType.Indoor,
            90,
            true);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            1,
            new TimeOnly(7, 0),
            new TimeOnly(17, 0),
            false));
        poi.AddTag(tag);
        poi.LinkTripadvisorLocation(454_980);
        poi.ConfigurePlanningMetadata(
            60_000m,
            "https://danangfantasticity.com/cham-museum",
            new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(7)));
        poi.ReviewIndoorOutdoor(
            IndoorOutdoorType.Indoor,
            new DateTimeOffset(2026, 10, 1, 11, 0, 0, TimeSpan.FromHours(7)));

        var before = CaptureState(poi);
        var occurredAt = new DateTimeOffset(2026, 10, 2, 15, 45, 0, TimeSpan.FromHours(7));

        var changed = poi.TryDeactivate(occurredAt);

        changed.Should().BeTrue();
        poi.Status.Should().Be(PointOfInterestStatus.Inactive);
        poi.UpdatedAtUtc.Should().Be(occurredAt.ToUniversalTime());
        AssertUnrelatedStateMatches(poi, before);
    }

    [Fact]
    public void TryDeactivate_InactivePoi_ReturnsFalseWithoutChangingDataOrTimestamp()
    {
        var poi = CreatePoi();
        var firstOccurredAt = new DateTimeOffset(
            2026,
            10,
            2,
            15,
            45,
            0,
            TimeSpan.FromHours(7));
        poi.TryDeactivate(firstOccurredAt).Should().BeTrue();
        var beforeRepeat = CaptureState(poi);

        var changed = poi.TryDeactivate(firstOccurredAt.AddDays(1));

        changed.Should().BeFalse();
        AssertEntireStateMatches(poi, beforeRepeat);
    }

    [Fact]
    public void PublicApi_DoesNotExposeDeleteStatusSetterOrReactivationMethods()
    {
        var publicMethodNames = typeof(PointOfInterest)
            .GetMethods()
            .Select(method => method.Name);

        publicMethodNames.Should().NotContain("Delete");
        publicMethodNames.Should().NotContain("SetStatus");
        publicMethodNames.Should().NotContain("Reactivate");
    }

    private static PointOfInterest CreatePoi() =>
        PointOfInterest.Create(
            PoiCategory.Create("Natural landmark", null),
            "Marble Mountains",
            16.003892m,
            108.264170m,
            42,
            CreatedAtUtc);

    private static PointOfInterestState CaptureState(PointOfInterest poi) =>
        new(
            poi.Id,
            poi.CategoryId,
            poi.Category,
            poi.Name,
            poi.Description,
            poi.Latitude,
            poi.Longitude,
            poi.Address,
            poi.IndoorOutdoor,
            poi.IndoorOutdoorReviewedAtUtc,
            poi.ScenicScore,
            poi.PhotoRating,
            poi.AverageVisitDurationMinutes,
            poi.HasShelter,
            poi.EstimatedVisitCost,
            poi.SourceUrl,
            poi.VerifiedAtUtc,
            poi.TripadvisorLocationId,
            poi.Status,
            poi.CreatedById,
            poi.CreatedAtUtc,
            poi.UpdatedAtUtc,
            poi.OpeningHours.ToArray(),
            poi.PoiTags.ToArray());

    private static void AssertEntireStateMatches(
        PointOfInterest poi,
        PointOfInterestState expected)
    {
        AssertUnrelatedStateMatches(poi, expected);
        poi.Status.Should().Be(expected.Status);
        poi.UpdatedAtUtc.Should().Be(expected.UpdatedAtUtc);
    }

    private static void AssertUnrelatedStateMatches(
        PointOfInterest poi,
        PointOfInterestState expected)
    {
        poi.Id.Should().Be(expected.Id);
        poi.CategoryId.Should().Be(expected.CategoryId);
        poi.Category.Should().BeSameAs(expected.Category);
        poi.Name.Should().Be(expected.Name);
        poi.Description.Should().Be(expected.Description);
        poi.Latitude.Should().Be(expected.Latitude);
        poi.Longitude.Should().Be(expected.Longitude);
        poi.Address.Should().Be(expected.Address);
        poi.IndoorOutdoor.Should().Be(expected.IndoorOutdoor);
        poi.IndoorOutdoorReviewedAtUtc.Should().Be(expected.IndoorOutdoorReviewedAtUtc);
        poi.ScenicScore.Should().Be(expected.ScenicScore);
        poi.PhotoRating.Should().Be(expected.PhotoRating);
        poi.AverageVisitDurationMinutes.Should().Be(expected.AverageVisitDurationMinutes);
        poi.HasShelter.Should().Be(expected.HasShelter);
        poi.EstimatedVisitCost.Should().Be(expected.EstimatedVisitCost);
        poi.SourceUrl.Should().Be(expected.SourceUrl);
        poi.VerifiedAtUtc.Should().Be(expected.VerifiedAtUtc);
        poi.TripadvisorLocationId.Should().Be(expected.TripadvisorLocationId);
        poi.CreatedById.Should().Be(expected.CreatedById);
        poi.CreatedAtUtc.Should().Be(expected.CreatedAtUtc);
        poi.OpeningHours.Should().Equal(expected.OpeningHours);
        poi.PoiTags.Should().Equal(expected.PoiTags);
    }

    private sealed record PointOfInterestState(
        long Id,
        int CategoryId,
        PoiCategory Category,
        string Name,
        string? Description,
        decimal Latitude,
        decimal Longitude,
        string? Address,
        IndoorOutdoorType IndoorOutdoor,
        DateTimeOffset? IndoorOutdoorReviewedAtUtc,
        decimal? ScenicScore,
        decimal? PhotoRating,
        int AverageVisitDurationMinutes,
        bool HasShelter,
        decimal? EstimatedVisitCost,
        string? SourceUrl,
        DateTimeOffset? VerifiedAtUtc,
        long? TripadvisorLocationId,
        PointOfInterestStatus Status,
        long? CreatedById,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        PoiOpeningHour[] OpeningHours,
        PoiTag[] PoiTags);
}