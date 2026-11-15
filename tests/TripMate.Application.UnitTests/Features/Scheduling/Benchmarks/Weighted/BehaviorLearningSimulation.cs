using System.Diagnostics;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;

/// <summary>
/// A simulated traveler for the learning benchmark. What they declare is broad (or nothing); what they really
/// like and hate is hidden from the system and only shows in what they do with a suggested day.
/// </summary>
/// <param name="LikedTags">A stop with one of these tags (and no hated tag) is one they truly enjoy.</param>
/// <param name="HatedTags">A stop with one of these tags is one they remove from the itinerary.</param>
public sealed record SimulatedTraveler(
    string Id,
    TravelerPace Pace,
    IReadOnlyList<string> DeclaredInterests,
    IReadOnlyCollection<string> LikedTags,
    IReadOnlyCollection<string> HatedTags);

/// <summary>What the ranking may use in a simulated run.</summary>
public enum BehaviorLearningMode
{
    /// <summary>Rules only: declared interests, no AI, no behavior.</summary>
    RulesOnly,

    /// <summary>Rules plus the count-based behavior affinity (no AI).</summary>
    RulesWithCounting,

    /// <summary>AI ranking by declared interests only (UC-10 AI level 1).</summary>
    AiOnly,

    /// <summary>AI ranking with the behavior profile, plus the count-based affinity (UC-10 item 6).</summary>
    AiWithBehavior,
}

/// <summary>One simulated trip: what was suggested and how it matched the hidden taste.</summary>
public sealed record BehaviorLearningRound(
    int Round,
    int Stops,
    int OptionalStops,
    int LikedStops,
    int HatedStops,
    IReadOnlyList<string> StopNames,
    int LikedProfileSize,
    int DislikedProfileSize,
    double RankingMilliseconds)
{
    public double HitRate => OptionalStops == 0 ? 0d : (double)LikedStops / OptionalStops;

    public double HateRate => OptionalStops == 0 ? 0d : (double)HatedStops / OptionalStops;
}

/// <summary>
/// UC-10 item 6 acceptance (spec .scratch/uc10-behavior-learning §4, D5): a traveler with a hidden taste takes
/// several trips. After each one they remove the stops they hate (a Dislike from the itinerary edit) and visit
/// the rest (a weak Like from navigation). The ranking only ever sees those signals, through the production
/// weights, decay, netting and count-based affinity.
/// </summary>
public static class BehaviorLearningSimulation
{
    /// <summary>Days between two simulated trips: consecutive days of one stay in the city.</summary>
    public const int DaysBetweenTrips = 1;

    /// <summary>Six hidden tastes behind two broad declarations (or none), each at two paces.</summary>
    public static IReadOnlyList<SimulatedTraveler> DefaultTravelers()
    {
        (string Id, string[] Declared, string[] Liked, string[] Hated)[] tastes =
        [
            ("MuseumLover", ["culture"], ["museum", "history", "art"], ["architecture", "shopping"]),
            ("CityWalker", ["culture"], ["architecture", "photography"], ["museum", "history"]),
            ("Hiker", ["nature"], ["adventure", "hiking"], ["beach", "relaxation"]),
            ("BeachRelaxer", ["nature"], ["beach", "relaxation"], ["adventure"]),
            ("HeritageNoProfile", [], ["heritage", "history"], ["beach", "relaxation"]),
            ("PhotographerNoProfile", [], ["photography", "scenic"], ["museum"]),
        ];
        return tastes
            .SelectMany(taste => new[] { TravelerPace.Moderate, TravelerPace.Fast }
                .Select(pace => new SimulatedTraveler($"{taste.Id}-{pace}", pace, taste.Declared, taste.Liked, taste.Hated)))
            .ToArray();
    }

    /// <param name="rank">
    /// The AI ranking (candidate POI id → AI score) for the two AI modes; null for the rule modes. It receives
    /// exactly what production sends: the optional candidates, the declared interests and the behavior profile.
    /// </param>
    public static async Task<IReadOnlyList<BehaviorLearningRound>> RunAsync(
        GoldenScenario scenario,
        SimulatedTraveler traveler,
        WeightedGateConfiguration configuration,
        BehaviorLearningMode mode,
        int rounds,
        Func<PoiRankingRequest, CancellationToken, Task<IReadOnlyDictionary<long, decimal>>>? rank = null,
        CancellationToken cancellationToken = default)
    {
        bool usesAi = mode is BehaviorLearningMode.AiOnly or BehaviorLearningMode.AiWithBehavior;
        bool usesBehavior = mode is BehaviorLearningMode.RulesWithCounting or BehaviorLearningMode.AiWithBehavior;
        if (usesAi && rank is null)
        {
            throw new ArgumentNullException(nameof(rank), "The AI modes need a ranking function.");
        }

        var persona = new BenchmarkPersonaData(
            traveler.Id,
            traveler.Id,
            traveler.Pace,
            RiskToleranceLevel.Medium,
            traveler.DeclaredInterests,
            [],
            "Simulated traveler with a hidden taste.");
        string[] tokens = traveler.DeclaredInterests.Select(WeightedGateExecutor.NormalizeInterest).ToArray();
        var byId = scenario.Candidates.ToDictionary(candidate => candidate.Id);
        var optionalCandidates = scenario.Candidates
            .Where(candidate => !scenario.Input.MandatoryPoiIds.Contains(candidate.Id))
            .ToArray();
        var signals = new List<BehaviorSignal>();
        var results = new List<BehaviorLearningRound>();

        for (var round = 1; round <= rounds; round++)
        {
            DateTimeOffset now = scenario.Input.StartAtUtc.AddDays(DaysBetweenTrips * (round - 1));
            PoiRankingBehavior? profile = mode == BehaviorLearningMode.AiWithBehavior
                ? Profile(signals, now, byId, configuration.Ranking)
                : null;

            // Decision D8, as in PoiRankingOrchestrator: a stop removed recently is not suggested again, unless
            // nothing else is left to suggest.
            IReadOnlySet<long> removedRecently = usesBehavior
                ? BehaviorProfileBuilder.RemovedRecently(signals, now, configuration.Ranking.RemovedStopCooldownDays)
                : new HashSet<long>();
            GenerationCandidate[] suggestible = optionalCandidates
                .Where(candidate => !removedRecently.Contains(candidate.Id))
                .ToArray();
            if (suggestible.Length == 0)
            {
                suggestible = optionalCandidates;
                removedRecently = new HashSet<long>();
            }

            IReadOnlyDictionary<long, decimal>? aiScores = null;
            double rankingMilliseconds = 0d;
            // Production consults the provider only when there are declared interests or a behavior profile.
            if (usesAi && (tokens.Length > 0 || profile is not null))
            {
                var request = new PoiRankingRequest(
                    suggestible
                        .Select(candidate => new PoiRankingCandidate(
                            candidate.Id,
                            candidate.Name,
                            candidate.CategoryName ?? string.Empty,
                            candidate.Tags?.ToArray() ?? []))
                        .ToArray(),
                    new PoiRankingContext(tokens, profile));
                var clock = Stopwatch.StartNew();
                aiScores = await rank!(request, cancellationToken);
                rankingMilliseconds = clock.Elapsed.TotalMilliseconds;
            }

            WeightedGateRun run = await WeightedGateExecutor.RunAsync(
                scenario,
                persona,
                configuration,
                warmupIterations: 0,
                measuredIterations: 1,
                cancellationToken,
                aiScores,
                usesBehavior ? CountBasedAffinities(signals, scenario.Candidates, configuration.Ranking) : null,
                removedRecently,
                // As in production: in a large city only the best-ranked candidates reach the solver.
                configuration.Generation.EffectiveMaxMatrixCandidates);

            GenerationCandidate[] optionalStops = run.OptionalVisitPoiIds.Select(id => byId[id]).ToArray();
            GenerationCandidate[] hated = optionalStops.Where(stop => IsHated(stop, traveler)).ToArray();
            results.Add(new BehaviorLearningRound(
                round,
                run.StopCount,
                optionalStops.Length,
                optionalStops.Count(stop => IsLiked(stop, traveler)),
                hated.Length,
                run.VisitPoiIds.Select(id => byId[id].Name).ToArray(),
                profile?.Liked.Count ?? 0,
                profile?.Disliked.Count ?? 0,
                rankingMilliseconds));

            // The traveler edits the itinerary (removes what they hate), then travels the rest.
            signals.AddRange(hated.Select(stop => new BehaviorSignal(
                stop.Id, RecommendationEventType.Dislike, RecommendationCaptureSource.ItineraryEdit, now)));
            signals.AddRange(optionalStops.Except(hated).Select(stop => new BehaviorSignal(
                stop.Id, RecommendationEventType.Like, RecommendationCaptureSource.Navigation, now)));
        }

        return results;
    }

    public static bool IsHated(GenerationCandidate stop, SimulatedTraveler traveler) =>
        Matches(stop, traveler.HatedTags);

    /// <summary>A hated tag wins: a museum with nice photo spots is still a museum to someone who hates museums.</summary>
    public static bool IsLiked(GenerationCandidate stop, SimulatedTraveler traveler) =>
        !IsHated(stop, traveler) && Matches(stop, traveler.LikedTags);

    private static bool Matches(GenerationCandidate stop, IReadOnlyCollection<string> tags) =>
        (stop.Tags ?? []).Any(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

    /// <summary>The profile production would send: same weights, decay, netting and per-side cap.</summary>
    private static PoiRankingBehavior? Profile(
        IReadOnlyCollection<BehaviorSignal> signals,
        DateTimeOffset now,
        IReadOnlyDictionary<long, GenerationCandidate> byId,
        PersonalizationRankingOptions options)
    {
        var weighted = BehaviorProfileBuilder.NetWeights(signals, now, options);
        PoiRankingBehaviorPoi[] Side(Func<decimal, bool> keep) => weighted
            .Where(poi => keep(poi.Net))
            .Take(options.MaxBehaviorPoisPerSide)
            .Select(poi => new PoiRankingBehaviorPoi(
                poi.PoiId,
                byId[poi.PoiId].Name,
                byId[poi.PoiId].CategoryName ?? string.Empty,
                byId[poi.PoiId].Tags?.ToArray() ?? [],
                Math.Abs(poi.Net)))
            .ToArray();

        var profile = new PoiRankingBehavior(Side(net => net > 0m), Side(net => net < 0m));
        return profile.Liked.Count == 0 && profile.Disliked.Count == 0 ? null : profile;
    }

    /// <summary>Production's count-based affinity per candidate (POI evidence first, then its category).</summary>
    private static Dictionary<long, decimal> CountBasedAffinities(
        IReadOnlyCollection<BehaviorSignal> signals,
        IReadOnlyList<GenerationCandidate> candidates,
        PersonalizationRankingOptions options)
    {
        string[] categories = candidates.Select(candidate => candidate.CategoryName ?? string.Empty).Distinct().ToArray();
        int CategoryId(GenerationCandidate candidate) => Array.IndexOf(categories, candidate.CategoryName ?? string.Empty);
        var byId = candidates.ToDictionary(candidate => candidate.Id);

        static int Count(IEnumerable<BehaviorSignal> group, RecommendationEventType type) =>
            group.Count(signal => signal.EventType == type);

        var aggregation = new PersonalBehaviorAggregation(
            signals.GroupBy(signal => signal.PoiId).ToDictionary(
                group => group.Key,
                group => new PersonalBehaviorCounts(
                    Count(group, RecommendationEventType.Like),
                    Count(group, RecommendationEventType.Dislike),
                    Count(group, RecommendationEventType.Skip))),
            signals.GroupBy(signal => CategoryId(byId[signal.PoiId])).ToDictionary(
                group => group.Key,
                group => new CategoryBehaviorCounts(
                    Count(group, RecommendationEventType.Like),
                    Count(group, RecommendationEventType.Dislike),
                    Count(group, RecommendationEventType.Skip),
                    group.Select(signal => signal.PoiId).Distinct().Count())));
        var scorer = new PersonalBehaviorAffinityScorer(options);
        return candidates.ToDictionary(
            candidate => candidate.Id,
            candidate => scorer.Score(candidate.Id, CategoryId(candidate), aggregation));
    }
}