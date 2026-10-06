using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public sealed class SchedulingGenerationSnapshotTests
{
    [Fact]
    public void Create_WithEquivalentUnorderedCollections_ProducesSameHash()
    {
        var original = CreateData();
        var reordered = original with
        {
            Pois = original.Pois
                .Reverse()
                .Select(poi => poi with
                {
                    OpeningHours = poi.OpeningHours.Reverse().ToArray(),
                    Tags = poi.Tags.Reverse().ToArray(),
                })
                .ToArray(),
        };

        var first = SchedulingGenerationSnapshot.Create(original);
        var second = SchedulingGenerationSnapshot.Create(reordered);

        first.Hash.Should().Be(second.Hash);
        first.Hash.Should().HaveLength(64);
    }

    [Theory]
    [MemberData(nameof(GenerationRelevantChanges))]
    public void Create_WhenGenerationRelevantValueChanges_ProducesDifferentHash(
        object changedData)
    {
        var changed = (SchedulingGenerationSnapshotData)changedData;
        var baseline = SchedulingGenerationSnapshot.Create(CreateData());

        var actual = SchedulingGenerationSnapshot.Create(changed);

        actual.Hash.Should().NotBe(baseline.Hash);
    }

    public static IEnumerable<object[]> GenerationRelevantChanges()
    {
        var baseline = CreateData();
        var poi = baseline.Pois.Single(item => item.PoiId == 10);
        var openingHours = poi.OpeningHours.ToArray();
        var tags = poi.Tags.ToArray();

        yield return Changed(baseline with { PreferenceTokens = ["museum"] });
        yield return Changed(baseline, poi with { PoiId = 99 });
        yield return Changed(baseline, poi with { Status = PointOfInterestStatus.Inactive });
        yield return Changed(baseline, poi with { Name = "Changed name" });
        yield return Changed(baseline, poi with { CategoryId = 99 });
        yield return Changed(baseline, poi with { CategoryName = "Museum" });
        yield return Changed(baseline, poi with { Latitude = 16.2m });
        yield return Changed(baseline, poi with { Longitude = 108.3m });
        yield return Changed(baseline, poi with { AverageVisitDurationMinutes = 90 });
        yield return Changed(baseline, poi with { EstimatedVisitCost = 75_000m });
        yield return Changed(baseline, poi with { ScenicScore = 4.9m });
        yield return Changed(baseline, poi with { PhotoRating = 4.8m });
        yield return Changed(baseline, poi with { HasShelter = false });
        yield return Changed(baseline, poi with { SourceUrl = null });
        yield return Changed(baseline, poi with { VerifiedAtUtc = null });
        yield return Changed(baseline, poi with
        {
            OpeningHours =
            [
                openingHours[0] with { OpenTime = new TimeOnly(9, 0) },
                openingHours[1],
            ],
        });
        yield return Changed(baseline, poi with
        {
            Tags = [tags[0] with { Name = "history" }, tags[1]],
        });
        yield return Changed(baseline with
        {
            Behavior = [baseline.Behavior.Single() with { LikeCount = 2 }],
        });
    }

    private static object[] Changed(SchedulingGenerationSnapshotData changed) => [changed];

    private static object[] Changed(
        SchedulingGenerationSnapshotData baseline,
        SchedulingGenerationSnapshotPoi changedPoi) =>
        [baseline with
        {
            Pois = baseline.Pois
                .Select(poi => poi.PoiId == 10 ? changedPoi : poi)
                .ToArray(),
        }];

    private static SchedulingGenerationSnapshotData CreateData()
    {
        var first = new SchedulingGenerationSnapshotPoi(
            PoiId: 10,
            Status: PointOfInterestStatus.Active,
            Name: "Beach",
            CategoryId: 2,
            CategoryName: "Outdoor",
            Latitude: 16.1m,
            Longitude: 108.2m,
            AverageVisitDurationMinutes: 60,
            EstimatedVisitCost: 50_000m,
            ScenicScore: 4.5m,
            PhotoRating: 4.4m,
            HasShelter: true,
            SourceUrl: "https://example.test/beach",
            VerifiedAtUtc: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            OpeningHours:
            [
                new(1, new TimeOnly(8, 0), new TimeOnly(17, 0), false),
                new(2, null, null, true),
            ],
            Tags:
            [
                new(3, "beach"),
                new(1, "outdoor"),
            ]);
        var second = first with { PoiId = 20, Name = "Cafe" };
        return new SchedulingGenerationSnapshotData(
            ["beach"],
            [first, second],
            [new(BehaviorScope.Poi, 10, 1, 0, 0, 0)]);
    }
}