using FluentAssertions;

using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Engine;
using TripMate.Application.Features.Rerouting.Engine.Strategies;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.Features.Rerouting.Engine.StrategyTestSupport;

namespace TripMate.Application.UnitTests.Features.Rerouting.Engine;

public sealed class ReroutingStrategyTests
{
    // ---------------------------------------------------------------- SevereWeather

    [Fact]
    public async Task SevereWeather_OffersIndoorReplacementsThenDrop()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: IndoorOutdoorType.Outdoor, bReviewed: true);
        var near = await kit.AddPoiAsync("Near indoor", ReroutingTestKit.NorthByKm(0.2), "Museum", IndoorOutdoorType.Indoor, reviewed: true);
        var far = await kit.AddPoiAsync("Far indoor", ReroutingTestKit.NorthByKm(2), "Museum", IndoorOutdoorType.Indoor, reviewed: true);
        var session = await kit.StartSessionAsync(a, b, c);

        var batches = await new SevereWeatherStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, b), CancellationToken.None);

        batches.Should().HaveCount(2);
        batches[0].Should().OnlyContain(variant => variant.Strategy == ReroutingStrategies.ReplaceWithIndoor);
        batches[0].Select(Ids).Should().BeEquivalentTo(
            new[] { new[] { a.Id, near.Id, c.Id }, new[] { a.Id, far.Id, c.Id } }, options => options.WithStrictOrdering());
        batches[0].Should().OnlyContain(variant => variant.RemovedPoiIds.SequenceEqual(new[] { b.Id }));
        batches[1].Should().ContainSingle();
        batches[1][0].Strategy.Should().Be(ReroutingStrategies.Drop);
        Ids(batches[1][0]).Should().Equal(a.Id, c.Id);
    }

    [Fact]
    public async Task SevereWeather_WithoutIndoorCandidates_OffersOnlyTheDrop()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: IndoorOutdoorType.Outdoor, bReviewed: true);
        _ = await kit.AddPoiAsync("Unreviewed indoor", ReroutingTestKit.NorthByKm(0.2), "Museum", IndoorOutdoorType.Indoor, reviewed: false);
        _ = await kit.AddPoiAsync("Indoor too far", ReroutingTestKit.NorthByKm(9), "Museum", IndoorOutdoorType.Indoor, reviewed: true);
        var session = await kit.StartSessionAsync(a, b, c);

        var batches = await new SevereWeatherStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, b), CancellationToken.None);

        batches.Should().ContainSingle();
        batches[0][0].Strategy.Should().Be(ReroutingStrategies.Drop);
    }

    [Fact]
    public async Task SevereWeather_LimitsReplacementsPerStop()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: IndoorOutdoorType.Outdoor, bReviewed: true);
        for (var index = 1; index <= 5; index++)
        {
            await kit.AddPoiAsync($"Indoor {index}", ReroutingTestKit.NorthByKm(index * 0.3), "Museum", IndoorOutdoorType.Indoor, reviewed: true);
        }

        var session = await kit.StartSessionAsync(a, b, c);

        var batches = await new SevereWeatherStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, b, maxReplacementsPerStop: 2), CancellationToken.None);

        batches[0].Should().HaveCount(2);
    }

    [Theory]
    [InlineData(IndoorOutdoorType.Indoor, true, false)]
    [InlineData(IndoorOutdoorType.Outdoor, false, false)]
    [InlineData(IndoorOutdoorType.Outdoor, true, true)]
    public async Task SevereWeather_OnlyActsOnAReviewedOutdoorNonMandatoryStop(
        IndoorOutdoorType kind,
        bool reviewed,
        bool mandatory)
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: kind, bReviewed: reviewed);
        await kit.AddPoiAsync("Indoor", ReroutingTestKit.NorthByKm(0.2), "Museum", IndoorOutdoorType.Indoor, reviewed: true);
        await kit.AddRequestAsync(mandatory ? [b.Id] : []);
        var session = await kit.StartSessionAsync(a, b, c);

        var batches = await new SevereWeatherStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, b), CancellationToken.None);

        batches.Should().BeEmpty();
    }

    [Fact]
    public async Task SevereWeather_WithASinglePendingStop_OffersNoDrop()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var only = await kit.AddPoiAsync("Only stop", ReroutingTestKit.NorthByKm(0), indoorOutdoor: IndoorOutdoorType.Outdoor, reviewed: true);
        await kit.AddPoiAsync("Indoor", ReroutingTestKit.NorthByKm(0.2), "Museum", IndoorOutdoorType.Indoor, reviewed: true);
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(only);

        var batches = await new SevereWeatherStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, only), CancellationToken.None);

        batches.Should().ContainSingle();
        batches[0][0].Strategy.Should().Be(ReroutingStrategies.ReplaceWithIndoor);
    }

    // ---------------------------------------------------------------- POIClosure

    [Fact]
    public async Task PoiClosure_OffersSameCategoryReplacementThenPostponeThenDrop()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit);
        var sameCategory = await kit.AddPoiAsync("Other culture", ReroutingTestKit.NorthByKm(0.4), "Culture");
        _ = await kit.AddPoiAsync("Other category", ReroutingTestKit.NorthByKm(0.2), "Nature");
        var session = await kit.StartSessionAsync(a, b, c);

        var batches = await new PoiClosureStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, b), CancellationToken.None);

        batches.Should().HaveCount(3);
        batches[0].Should().ContainSingle();
        batches[0][0].Strategy.Should().Be(ReroutingStrategies.ReplaceSameCategory);
        Ids(batches[0][0]).Should().Equal(a.Id, sameCategory.Id, c.Id);
        batches[0][0].RemovedPoiIds.Should().Equal(b.Id);
        batches[1].Should().ContainSingle();
        batches[1][0].Strategy.Should().Be(ReroutingStrategies.Postpone);
        Ids(batches[1][0]).Should().Equal(a.Id, c.Id, b.Id);
        batches[1][0].RemovedPoiIds.Should().BeEmpty();
        batches[2][0].Strategy.Should().Be(ReroutingStrategies.Drop);
        Ids(batches[2][0]).Should().Equal(a.Id, c.Id);
    }

    [Fact]
    public async Task PoiClosure_PostponesToEveryLaterSlot()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var stops = new List<PointOfInterest>();
        for (var index = 0; index < 4; index++)
        {
            stops.Add(await kit.AddPoiAsync($"Stop {index + 1}", ReroutingTestKit.NorthByKm(index * 0.2)));
        }

        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(stops.ToArray());

        var batches = await new PoiClosureStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, stops[0]), CancellationToken.None);

        var postpone = batches.Single(batch => batch[0].Strategy == ReroutingStrategies.Postpone);
        postpone.Select(Ids).Should().BeEquivalentTo(
            new[]
            {
                new[] { stops[1].Id, stops[0].Id, stops[2].Id, stops[3].Id },
                new[] { stops[1].Id, stops[2].Id, stops[0].Id, stops[3].Id },
                new[] { stops[1].Id, stops[2].Id, stops[3].Id, stops[0].Id },
            },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task PoiClosure_ForAMandatoryStop_OnlyPostpones()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, mandatoryB: true);
        await kit.AddPoiAsync("Other culture", ReroutingTestKit.NorthByKm(0.4), "Culture");
        var session = await kit.StartSessionAsync(a, b, c);

        var batches = await new PoiClosureStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, b), CancellationToken.None);

        batches.Should().ContainSingle();
        batches[0][0].Strategy.Should().Be(ReroutingStrategies.Postpone);
    }

    [Fact]
    public async Task PoiClosure_ForTheLastStopWithNoReplacement_OnlyDrops()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit);
        var session = await kit.StartSessionAsync(a, b, c);

        var batches = await new PoiClosureStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, c), CancellationToken.None);

        batches.Should().ContainSingle();
        batches[0][0].Strategy.Should().Be(ReroutingStrategies.Drop);
        Ids(batches[0][0]).Should().Equal(a.Id, b.Id);
    }

    // ---------------------------------------------------------------- ScheduleDelay

    [Fact]
    public async Task ScheduleDelay_StartsWithAProbeOfTheCurrentOrderThenSingleDropsLeastPreferredFirst()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit);
        var session = await kit.StartSessionAsync(a, b, c);
        var scores = new Dictionary<long, int> { [a.Id] = 200, [b.Id] = 100, [c.Id] = 0 };

        var batches = await new ScheduleDelayStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, a, preferenceScores: scores), CancellationToken.None);

        batches.Should().HaveCount(2);
        batches[0][0].IsBaseProbe.Should().BeTrue();
        Ids(batches[0][0]).Should().Equal(a.Id, b.Id, c.Id);
        batches[0].Skip(1).Should().OnlyContain(variant => variant.Strategy == ReroutingStrategies.ReduceStops && !variant.IsBaseProbe);
        batches[0].Skip(1).Select(variant => variant.RemovedPoiIds.Single()).Should().Equal(c.Id, b.Id, a.Id);
        batches[1].Should().OnlyContain(variant => variant.RemovedPoiIds.Count == 2);
        batches[1].Select(variant => variant.RemovedPoiIds.ToArray()).Should().BeEquivalentTo(
            new[] { new[] { c.Id, b.Id }, new[] { c.Id, a.Id }, new[] { b.Id, a.Id } }, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task ScheduleDelay_NeverDropsAMandatoryStop()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, mandatoryB: true);
        var session = await kit.StartSessionAsync(a, b, c);

        var batches = await new ScheduleDelayStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, a), CancellationToken.None);

        batches.SelectMany(batch => batch).SelectMany(variant => variant.RemovedPoiIds).Should().NotContain(b.Id);
        batches.SelectMany(batch => batch).Should().OnlyContain(variant => Ids(variant).Contains(b.Id));
    }

    [Fact]
    public async Task ScheduleDelay_WithASinglePendingStop_OnlyProbes()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var only = await kit.AddPoiAsync("Only stop");
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(only);

        var batches = await new ScheduleDelayStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, only), CancellationToken.None);

        batches.Should().ContainSingle();
        batches[0].Should().ContainSingle(variant => variant.IsBaseProbe);
    }

    [Fact]
    public async Task ScheduleDelay_BoundsTheVariantsByMaxVariants()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var stops = new List<PointOfInterest>();
        for (var index = 0; index < 6; index++)
        {
            stops.Add(await kit.AddPoiAsync($"Stop {index + 1}", ReroutingTestKit.NorthByKm(index * 0.2)));
        }

        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(stops.ToArray());

        var batches = await new ScheduleDelayStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, stops[0], maxVariants: 3), CancellationToken.None);

        batches[0].Should().HaveCount(3, "one probe and two single drops");
        batches[1].Should().HaveCount(3);
    }

    // ---------------------------------------------------------------- RouteDeviation

    [Fact]
    public async Task RouteDeviation_OffersShorterOrdersFirstBestFirst()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        // Request start is the centre. Current order goes far, near, mid; the nearest-first order is shorter.
        var far = await kit.AddPoiAsync("Far", ReroutingTestKit.NorthByKm(3));
        var near = await kit.AddPoiAsync("Near", ReroutingTestKit.NorthByKm(0.5));
        var mid = await kit.AddPoiAsync("Mid", ReroutingTestKit.NorthByKm(1.5));
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(far, near, mid);

        var batches = await new RouteDeviationStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, far), CancellationToken.None);

        batches[0].Should().OnlyContain(variant => variant.Strategy == ReroutingStrategies.Reorder);
        Ids(batches[0][0]).Should().Equal(near.Id, mid.Id, far.Id);
        batches[0].Should().OnlyContain(variant => Ids(variant).OrderBy(id => id).SequenceEqual(new[] { far.Id, near.Id, mid.Id }.OrderBy(id => id)));
        batches[0].Select(Ids).Should().NotContain(order => order.SequenceEqual(new[] { far.Id, near.Id, mid.Id }));
        batches.SelectMany(batch => batch).Should().Contain(variant => variant.IsBaseProbe && variant.Strategy == ReroutingStrategies.ReduceStops,
            "ReduceStops follows the reorders as the fallback");
    }

    [Fact]
    public async Task RouteDeviation_NeverOffersAnOrderThatIsNotShorterThanTheCurrentOne()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var near = await kit.AddPoiAsync("Near", ReroutingTestKit.NorthByKm(0.5));
        var mid = await kit.AddPoiAsync("Mid", ReroutingTestKit.NorthByKm(1.5));
        var far = await kit.AddPoiAsync("Far", ReroutingTestKit.NorthByKm(3));
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(near, mid, far);

        var batches = await new RouteDeviationStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, near), CancellationToken.None);

        batches.SelectMany(batch => batch).Should().NotContain(variant => variant.Strategy == ReroutingStrategies.Reorder);
    }

    [Fact]
    public async Task RouteDeviation_KeepsEveryStopIncludingMandatoryOnes()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var far = await kit.AddPoiAsync("Far", ReroutingTestKit.NorthByKm(3));
        var near = await kit.AddPoiAsync("Near", ReroutingTestKit.NorthByKm(0.5));
        await kit.AddRequestAsync([far.Id]);
        var session = await kit.StartSessionAsync(far, near);

        var batches = await new RouteDeviationStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, far), CancellationToken.None);

        var reorder = batches.SelectMany(batch => batch).Where(variant => variant.Strategy == ReroutingStrategies.Reorder).ToArray();
        reorder.Should().ContainSingle();
        Ids(reorder[0]).Should().Equal(near.Id, far.Id);
        reorder[0].RemovedPoiIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RouteDeviation_WithManyStops_UsesOneNearestNeighbourOrder()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var stops = new List<PointOfInterest>();
        foreach (var km in new[] { 7.0, 6.0, 5.0, 4.0, 3.0, 2.0, 1.0 })
        {
            stops.Add(await kit.AddPoiAsync($"Stop at {km} km", ReroutingTestKit.NorthByKm(km)));
        }

        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(stops.ToArray());

        var batches = await new RouteDeviationStrategy().CreateBatchesAsync(
            await ContextAsync(kit, session, stops[0], radiusKm: 10m), CancellationToken.None);

        var reorder = batches[0];
        reorder.Should().ContainSingle();
        reorder[0].Strategy.Should().Be(ReroutingStrategies.Reorder);
        Ids(reorder[0]).Should().Equal(stops.AsEnumerable().Reverse().Select(poi => poi.Id));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(PointOfInterest A, PointOfInterest B, PointOfInterest C)> ThreeStopsAsync(
        ReroutingTestKit kit,
        IndoorOutdoorType bIndoorOutdoor = IndoorOutdoorType.Outdoor,
        bool bReviewed = false,
        bool mandatoryB = false)
    {
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), "Culture", bIndoorOutdoor, bReviewed);
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2));
        if (kit.Request is null || mandatoryB)
        {
            await kit.AddRequestAsync(mandatoryB ? [b.Id] : []);
        }

        return (a, b, c);
    }
}