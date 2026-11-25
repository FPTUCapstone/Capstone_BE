using FluentAssertions;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Rerouting.Engine;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Rerouting.Engine;

public sealed class ReroutingAnchorFactoryTests
{
    private static readonly DateTimeOffset Start = ReroutingTestKit.RequestStart;

    [Fact]
    public async Task Create_WhenNothingWasReached_StartsAtTheRequestStartPointAndNow()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, pois) = await StartAsync(kit, 3);
        var now = Start.AddMinutes(30);

        var anchor = Create(kit, session, pois[1], now);

        anchor.StartAtUtc.Should().Be(now);
        anchor.Start.Latitude.Should().Be(ReroutingTestKit.CenterLatitude);
        anchor.Start.Longitude.Should().Be(ReroutingTestKit.CenterLongitude);
        anchor.AvailableMinutes.Should().Be(450, "the day ends 480 minutes after the request start");
    }

    [Fact]
    public async Task Create_WhenNavigatingAfterAStop_StartsAtTheMostRecentlyReachedStop()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, pois) = await StartAsync(kit, 4);
        session.ReachItem(kit.ItemIdOf(session, pois[0]), Start.AddMinutes(20));
        session.Depart(Start.AddMinutes(80));
        session.ReachItem(kit.ItemIdOf(session, pois[1]), Start.AddMinutes(100));
        session.Depart(Start.AddMinutes(150));
        var now = Start.AddMinutes(160);

        var anchor = Create(kit, session, pois[3], now);

        session.FsmState.Should().Be(TripSession.NavigatingState);
        anchor.StartAtUtc.Should().Be(now);
        anchor.Start.Latitude.Should().Be(pois[1].Latitude);
    }

    [Fact]
    public async Task Create_WhenExploringBeforeThePlannedDeparture_StartsAtThePlannedDeparture()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, pois) = await StartAsync(kit, 3);
        var exploring = session.Items.Single(item => item.PoiId == pois[0].Id);
        session.ReachItem(exploring.ItineraryItemId, Start.AddMinutes(15));
        var now = Start.AddMinutes(30);

        var anchor = Create(kit, session, pois[2], now);

        session.FsmState.Should().Be(TripSession.ExploringState);
        exploring.PlannedDepartureUtc.Should().BeAfter(now);
        anchor.StartAtUtc.Should().Be(exploring.PlannedDepartureUtc);
        anchor.Start.Latitude.Should().Be(pois[0].Latitude);
        anchor.AvailableMinutes.Should().Be(480 - 70);
    }

    [Fact]
    public async Task Create_WhenExploringAndAlreadyOverstaying_StartsAtNow()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, pois) = await StartAsync(kit, 3);
        var exploring = session.Items.Single(item => item.PoiId == pois[0].Id);
        session.ReachItem(exploring.ItineraryItemId, Start.AddMinutes(15));
        var now = exploring.PlannedDepartureUtc.AddMinutes(25);

        var anchor = Create(kit, session, pois[2], now);

        anchor.StartAtUtc.Should().Be(now);
    }

    [Theory]
    [InlineData(800_000, 680_000)]
    [InlineData(100_000, 0)]
    public async Task Create_DeductsTheCostOfReachedStopsFromTheBudget(int budget, int expected)
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, pois) = await StartAsync(kit, 4, budget);
        session.ReachItem(kit.ItemIdOf(session, pois[0]), Start.AddMinutes(20));
        session.Depart(Start.AddMinutes(80));
        session.ReachItem(kit.ItemIdOf(session, pois[1]), Start.AddMinutes(100));
        session.SkipItem(kit.ItemIdOf(session, pois[2]), Start.AddMinutes(101));

        var anchor = Create(kit, session, pois[3], Start.AddMinutes(160));

        anchor.RemainingBudgetVnd.Should().Be(expected, "reached stops cost 60,000 each; a skipped stop costs nothing");
    }

    [Fact]
    public async Task Create_WithoutABudget_HasNoRemainingBudget()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, pois) = await StartAsync(kit, 2, budget: null);

        Create(kit, session, pois[1], Start.AddMinutes(30)).RemainingBudgetVnd.Should().BeNull();
    }

    [Fact]
    public async Task Create_AfterTheDayEnd_HasNoMinutesLeft()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, pois) = await StartAsync(kit, 2);

        Create(kit, session, pois[1], Start.AddMinutes(500)).AvailableMinutes.Should().Be(0);
    }

    [Fact]
    public async Task Create_BoundsCandidatesAroundTheAffectedStopWithTheGivenRadius()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, pois) = await StartAsync(kit, 3);

        var anchor = ReroutingAnchorFactory.Create(
            session, kit.Request, session.Items.Single(item => item.PoiId == pois[2].Id), Costs(session), 7.5m, Start);

        anchor.CandidateRadiusKm.Should().Be(7.5m);
        anchor.CandidateCenter.Latitude.Should().Be(pois[2].Latitude);
        anchor.CandidateCenter.Longitude.Should().Be(pois[2].Longitude);
    }

    private static async Task<(TripSession Session, PointOfInterest[] Pois)> StartAsync(
        ReroutingTestKit kit,
        int count,
        decimal? budget = 800_000m)
    {
        var pois = new List<PointOfInterest>();
        for (var index = 0; index < count; index++)
        {
            pois.Add(await kit.AddPoiAsync($"Stop {index + 1}"));
        }

        await kit.AddRequestAsync(budgetVnd: budget);
        return (await kit.StartSessionAsync(pois.ToArray()), pois.ToArray());
    }

    private static EvaluationAnchor Create(
        ReroutingTestKit kit,
        TripSession session,
        PointOfInterest affected,
        DateTimeOffset now) =>
        ReroutingAnchorFactory.Create(
            session, kit.Request, session.Items.Single(item => item.PoiId == affected.Id), Costs(session), 5m, now);

    private static Dictionary<long, decimal?> Costs(TripSession session) =>
        session.Items.ToDictionary(item => item.ItineraryItemId, _ => (decimal?)60_000m);
}