using FluentAssertions;

using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;

/// <summary>
/// UC-10 item 6: the mechanics of the learning benchmark, with a stand-in AI so the test needs no model. The
/// real measurement runs against the team AI service (<c>weighted-gate --behavior-learning</c>).
/// </summary>
public sealed class BehaviorLearningSimulationTests
{
    private static readonly GoldenScenario Scenario =
        SchedulingBenchmarkScenarioFactory.CreateScenario(SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData());

    private static readonly WeightedGateConfiguration Configuration = WeightedGateConfiguration.LoadShipped(
        WeightedGateConfiguration.FindRepositoryRoot(AppContext.BaseDirectory));

    private static readonly SimulatedTraveler MuseumLover = new(
        "MuseumLover",
        TravelerPace.Moderate,
        ["culture"],
        ["museum", "history", "art"],
        ["architecture", "shopping"]);

    [Fact]
    public async Task WithoutBehavior_EveryTripIsTheSameDay()
    {
        var rounds = await BehaviorLearningSimulation.RunAsync(
            Scenario, MuseumLover, Configuration, BehaviorLearningMode.RulesOnly, rounds: 3);

        rounds.Should().HaveCount(3);
        rounds.Select(round => string.Join("|", round.StopNames)).Distinct().Should().ContainSingle();
        rounds.Should().OnlyContain(round => round.LikedProfileSize == 0 && round.DislikedProfileSize == 0);
    }

    [Fact]
    public async Task WithBehavior_TheAiReceivesWhatTheTravelerRemovedAndVisited_FromTheSecondTrip()
    {
        var requests = new List<PoiRankingRequest>();

        var rounds = await BehaviorLearningSimulation.RunAsync(
            Scenario,
            MuseumLover,
            Configuration,
            BehaviorLearningMode.AiWithBehavior,
            rounds: 3,
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(TagOverlapRanking(request));
            });

        requests.Should().HaveCount(3);
        requests[0].Context.Behavior.Should().BeNull("nothing has happened before the first trip");
        requests[0].Context.PreferenceTokens.Should().Equal("culture");
        requests.Should().OnlyContain(request =>
            request.Candidates.All(candidate => !Scenario.Input.MandatoryPoiIds.Contains(candidate.PoiId)));

        var firstTripStops = Scenario.Candidates
            .Where(candidate => rounds[0].StopNames.Contains(candidate.Name)
                && !Scenario.Input.MandatoryPoiIds.Contains(candidate.Id))
            .ToArray();
        PoiRankingBehavior profile = requests[1].Context.Behavior!;
        profile.Disliked.Select(poi => poi.PoiId).Should().BeEquivalentTo(
            firstTripStops.Where(stop => BehaviorLearningSimulation.IsHated(stop, MuseumLover)).Select(stop => stop.Id));
        profile.Liked.Select(poi => poi.PoiId).Should().BeEquivalentTo(
            firstTripStops.Where(stop => !BehaviorLearningSimulation.IsHated(stop, MuseumLover)).Select(stop => stop.Id));
        // A removed stop is a full dislike; a visited one a weak like, already a day old at the next trip.
        profile.Disliked.Should().OnlyContain(poi => poi.Weight > 0.9m && poi.Weight <= 1.0m);
        profile.Liked.Should().OnlyContain(poi => poi.Weight > 0.25m && poi.Weight <= 0.3m);
        rounds[1].DislikedProfileSize.Should().Be(profile.Disliked.Count);
    }

    [Fact]
    public async Task WithBehavior_AndAnAiThatUsesTheProfile_TravelersGetFewerStopsTheyHate()
    {
        // Measured over all default travelers, like the real benchmark: one traveler alone may not improve, because
        // a short, cheap stop can stay in the plan even after the AI ranks it low (see the 2026-11-11 report).
        var firstTrips = new List<BehaviorLearningRound>();
        var lastTrips = new List<BehaviorLearningRound>();
        foreach (SimulatedTraveler traveler in BehaviorLearningSimulation.DefaultTravelers())
        {
            var rounds = await BehaviorLearningSimulation.RunAsync(
                Scenario,
                traveler,
                Configuration,
                BehaviorLearningMode.AiWithBehavior,
                rounds: 3,
                (request, _) => Task.FromResult(TagOverlapRanking(request)));
            firstTrips.Add(rounds[0]);
            lastTrips.Add(rounds[^1]);
        }

        firstTrips.Sum(trip => trip.HatedStops).Should().BeGreaterThan(
            0, "declared interests alone suggest stops these travelers hate");
        lastTrips.Sum(trip => trip.HatedStops).Should().BeLessThan(firstTrips.Sum(trip => trip.HatedStops));
    }

    [Theory]
    [InlineData(BehaviorLearningMode.RulesWithCounting)]
    [InlineData(BehaviorLearningMode.AiWithBehavior)]
    public async Task WithBehavior_AStopTheTravelerRemoved_IsNotSuggestedOnTheFollowingDays(BehaviorLearningMode mode)
    {
        // Decision D8 (14 days): the simulated trips are consecutive days, all inside the cooldown.
        var rounds = await BehaviorLearningSimulation.RunAsync(
            Scenario,
            MuseumLover,
            Configuration,
            mode,
            rounds: 3,
            (request, _) => Task.FromResult(TagOverlapRanking(request)));

        var removedOnDayOne = Scenario.Candidates
            .Where(candidate => rounds[0].StopNames.Contains(candidate.Name)
                && !Scenario.Input.MandatoryPoiIds.Contains(candidate.Id)
                && BehaviorLearningSimulation.IsHated(candidate, MuseumLover))
            .Select(candidate => candidate.Name)
            .ToArray();
        removedOnDayOne.Should().NotBeEmpty();
        rounds[1].StopNames.Should().NotContain(removedOnDayOne);
        rounds[2].StopNames.Should().NotContain(removedOnDayOne);
    }

    [Fact]
    public async Task TravelerWithoutDeclaredInterests_ConsultsTheAiOnlyOnceThereIsBehavior()
    {
        var calls = 0;
        var traveler = MuseumLover with { DeclaredInterests = [] };

        await BehaviorLearningSimulation.RunAsync(
            Scenario,
            traveler,
            Configuration,
            BehaviorLearningMode.AiWithBehavior,
            rounds: 2,
            (request, _) =>
            {
                calls++;
                request.Context.Behavior.Should().NotBeNull();
                return Task.FromResult(TagOverlapRanking(request));
            });

        calls.Should().Be(1, "the first trip has neither interests nor behavior, like production's provider skip");
    }

    [Fact]
    public void DefaultTravelers_AreTwelveWithHiddenTastesThatExistInTheData()
    {
        var travelers = BehaviorLearningSimulation.DefaultTravelers();

        travelers.Should().HaveCount(12);
        travelers.Select(traveler => traveler.Id).Should().OnlyHaveUniqueItems();
        foreach (SimulatedTraveler traveler in travelers)
        {
            Scenario.Candidates.Should().Contain(candidate => BehaviorLearningSimulation.IsLiked(candidate, traveler), traveler.Id);
            Scenario.Candidates.Should().Contain(candidate => BehaviorLearningSimulation.IsHated(candidate, traveler), traveler.Id);
        }
    }

    // The simulated look-alike city: enough similar places to tell semantic learning from "do not repeat".

    [Fact]
    public void LookAlikeCity_HasManyLookAlikesForEveryHiddenTaste()
    {
        GoldenScenario city = SchedulingBenchmarkScenarioFactory.CreateScenario(LookAlikeCityFactory.Create());

        city.Candidates.Should().HaveCount(54);
        city.Candidates.Select(candidate => candidate.Name).Should().OnlyHaveUniqueItems();
        city.Input.MandatoryPoiIds.Should().BeEmpty();
        foreach (SimulatedTraveler traveler in BehaviorLearningSimulation.DefaultTravelers())
        {
            city.Candidates.Count(candidate => BehaviorLearningSimulation.IsLiked(candidate, traveler))
                .Should().BeGreaterThanOrEqualTo(8, $"{traveler.Id} needs look-alikes to like");
            city.Candidates.Count(candidate => BehaviorLearningSimulation.IsHated(candidate, traveler))
                .Should().BeGreaterThanOrEqualTo(8, $"{traveler.Id} needs look-alikes to remove");
        }
    }

    [Fact]
    public async Task InALargeCity_OnlyTheBestRankedCandidatesReachTheSolver_LikeProduction()
    {
        GoldenScenario city = SchedulingBenchmarkScenarioFactory.CreateScenario(LookAlikeCityFactory.Create());
        long[] bestRanked = city.Candidates.Skip(20).Take(10).Select(candidate => candidate.Id).ToArray();
        var aiScores = city.Candidates.ToDictionary(
            candidate => candidate.Id,
            candidate => bestRanked.Contains(candidate.Id) ? 1m : 0m);
        var persona = new BenchmarkPersonaData(
            "NoInterests", "No interests", TravelerPace.Moderate, RiskToleranceLevel.Medium, [], [], "Cap test.");

        WeightedGateRun run = await WeightedGateExecutor.RunAsync(
            city, persona, Configuration, 0, 1, aiScores: aiScores, matrixCandidateCap: 10);

        run.VisitPoiIds.Should().NotBeEmpty();
        run.VisitPoiIds.Should().BeSubsetOf(bestRanked);
    }

    [Fact]
    public async Task InTheLookAlikeCity_RemovedStopsAndTheirCooldownWorkTheSame()
    {
        GoldenScenario city = SchedulingBenchmarkScenarioFactory.CreateScenario(LookAlikeCityFactory.Create());

        var rounds = await BehaviorLearningSimulation.RunAsync(
            city,
            MuseumLover,
            Configuration,
            BehaviorLearningMode.AiWithBehavior,
            rounds: 2,
            (request, _) => Task.FromResult(TagOverlapRanking(request)));

        rounds[0].Stops.Should().BeGreaterThan(0);
        var removedOnDayOne = city.Candidates
            .Where(candidate => rounds[0].StopNames.Contains(candidate.Name)
                && BehaviorLearningSimulation.IsHated(candidate, MuseumLover))
            .Select(candidate => candidate.Name)
            .ToArray();
        rounds[1].StopNames.Should().NotContain(removedOnDayOne);
    }

    /// <summary>
    /// A stand-in AI: 0.5, plus the share of a candidate's tags seen in liked POIs, minus the share seen in
    /// disliked ones. Enough to prove the profile reaches the ranking and moves the plan.
    /// </summary>
    private static IReadOnlyDictionary<long, decimal> TagOverlapRanking(PoiRankingRequest request)
    {
        static HashSet<string> Tags(IEnumerable<PoiRankingBehaviorPoi>? pois) =>
            (pois ?? []).SelectMany(poi => poi.TagNames).ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> liked = Tags(request.Context.Behavior?.Liked);
        HashSet<string> disliked = Tags(request.Context.Behavior?.Disliked);
        return request.Candidates.ToDictionary(
            candidate => candidate.PoiId,
            candidate =>
            {
                decimal count = Math.Max(1, candidate.TagNames.Count);
                decimal lean = (candidate.TagNames.Count(liked.Contains) - candidate.TagNames.Count(disliked.Contains)) / count;
                return Math.Clamp(0.5m + (0.5m * lean), 0m, 1m);
            });
    }
}