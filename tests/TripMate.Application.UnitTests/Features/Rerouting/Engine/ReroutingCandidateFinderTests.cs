using FluentAssertions;

using TripMate.Application.Features.Rerouting.Engine;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Rerouting.Engine;

public sealed class ReroutingCandidateFinderTests
{
    private static readonly IReadOnlySet<string> NoTokens = new HashSet<string>();

    [Fact]
    public async Task Find_OrdersSameCategoryFirstThenDistance()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var affected = await kit.AddPoiAsync("Affected", ReroutingTestKit.NorthByKm(0), "Culture");
        var nearOther = await kit.AddPoiAsync("Near nature", ReroutingTestKit.NorthByKm(0.5), "Nature");
        var farSame = await kit.AddPoiAsync("Far culture", ReroutingTestKit.NorthByKm(3), "Culture");
        var nearSame = await kit.AddPoiAsync("Near culture", ReroutingTestKit.NorthByKm(1), "Culture");

        var found = await Finder(kit).FindAsync(Query(affected), CancellationToken.None);

        found.Select(poi => poi.Id).Should().Equal(nearSame.Id, farSame.Id, nearOther.Id);
    }

    [Fact]
    public async Task Find_BreaksDistanceTiesByPreferenceScore()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var affected = await kit.AddPoiAsync("Affected", ReroutingTestKit.NorthByKm(0), "Culture");
        var plain = await kit.AddPoiAsync("Plain", ReroutingTestKit.NorthByKm(1), "Shopping");
        var liked = await kit.AddPoiAsync("Liked", ReroutingTestKit.NorthByKm(1), "Nature");

        var found = await Finder(kit).FindAsync(
            Query(affected) with { PreferenceTokens = new HashSet<string> { "nature" } },
            CancellationToken.None);

        found.Select(poi => poi.Id).Should().Equal(liked.Id, plain.Id);
    }

    [Fact]
    public async Task Find_StopsAtTheLimit()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var affected = await kit.AddPoiAsync("Affected", ReroutingTestKit.NorthByKm(0));
        for (var index = 1; index <= 5; index++)
        {
            await kit.AddPoiAsync($"Candidate {index}", ReroutingTestKit.NorthByKm(index * 0.2));
        }

        var found = await Finder(kit).FindAsync(Query(affected) with { Limit = 3 }, CancellationToken.None);

        found.Should().HaveCount(3);
    }

    [Fact]
    public async Task Find_SkipsTheAffectedStopExcludedPoisPoisOutsideTheRadiusAndUnreadyOnes()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var affected = await kit.AddPoiAsync("Affected", ReroutingTestKit.NorthByKm(0));
        var excluded = await kit.AddPoiAsync("Already in trip", ReroutingTestKit.NorthByKm(0.3));
        _ = await kit.AddPoiAsync("Outside radius", ReroutingTestKit.NorthByKm(9));
        _ = await kit.AddPoiAsync("Not planning ready", ReroutingTestKit.NorthByKm(0.4), planningReady: false);
        var ok = await kit.AddPoiAsync("Fine", ReroutingTestKit.NorthByKm(2));

        var found = await Finder(kit).FindAsync(
            Query(affected) with { ExcludedPoiIds = new HashSet<long> { excluded.Id } },
            CancellationToken.None);

        found.Select(poi => poi.Id).Should().Equal(ok.Id);
    }

    [Fact]
    public async Task Find_WithReviewedIndoorRequirement_KeepsOnlyReviewedIndoorPois()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var affected = await kit.AddPoiAsync("Affected", ReroutingTestKit.NorthByKm(0));
        var reviewedIndoor = await kit.AddPoiAsync("Reviewed indoor", ReroutingTestKit.NorthByKm(1), indoorOutdoor: IndoorOutdoorType.Indoor, reviewed: true);
        _ = await kit.AddPoiAsync("Unreviewed indoor", ReroutingTestKit.NorthByKm(0.5), indoorOutdoor: IndoorOutdoorType.Indoor, reviewed: false);
        _ = await kit.AddPoiAsync("Reviewed outdoor", ReroutingTestKit.NorthByKm(0.6), indoorOutdoor: IndoorOutdoorType.Outdoor, reviewed: true);

        var found = await Finder(kit).FindAsync(Query(affected) with { RequireReviewedIndoor = true }, CancellationToken.None);

        found.Select(poi => poi.Id).Should().Equal(reviewedIndoor.Id);
    }

    [Fact]
    public async Task Find_WithSameCategoryRequirement_KeepsOnlyThatCategory()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var affected = await kit.AddPoiAsync("Affected", ReroutingTestKit.NorthByKm(0), "Culture");
        var same = await kit.AddPoiAsync("Same category", ReroutingTestKit.NorthByKm(2), "Culture");
        _ = await kit.AddPoiAsync("Other category", ReroutingTestKit.NorthByKm(0.5), "Nature");

        var found = await Finder(kit).FindAsync(Query(affected) with { RequireSameCategory = true }, CancellationToken.None);

        found.Select(poi => poi.Id).Should().Equal(same.Id);
    }

    private static ReroutingCandidateFinder Finder(ReroutingTestKit kit) => new(kit.Db);

    private static ReroutingCandidateQuery Query(PointOfInterest affected) =>
        new(affected, 5m, new HashSet<long>(), NoTokens, 10);
}