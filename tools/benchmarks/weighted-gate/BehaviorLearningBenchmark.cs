using System.Globalization;
using System.Text;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiRanking;
using TripMate.Infrastructure.TripMateAi;

/// <summary>
/// UC-10 item 6 acceptance (spec .scratch/uc10-behavior-learning §4, D5): the learning curve of simulated travelers
/// against the real team AI service (bge-m3) through the production <see cref="TripMateAiPoiRankingProvider"/>, on the
/// real Da Nang fixture (acceptance) and on a larger simulated city with many look-alike places.
/// </summary>
internal static class BehaviorLearningBenchmark
{
    /// <summary>Criterion 1: the hidden-taste hit rate must rise by at least this many points from trip 1 to the last.</summary>
    private const double MinimumHitRateGain = 0.10;

    /// <summary>Criterion 2: on the first trip (no behavior yet) the new ranking may be at most this much worse.</summary>
    private const double FirstTripTolerance = 0.01;

    private const string Shipped = "C";
    private const string AiOnly = "B";
    private const string RulesWithCooldown = "A2";

    private sealed record Row(string Key, string Label, BehaviorLearningMode Mode, WeightedGateConfiguration Configuration);

    private sealed record Curve(
        GoldenScenario Scenario,
        IReadOnlyList<SimulatedTraveler> Travelers,
        IReadOnlyDictionary<(string Traveler, string Row), IReadOnlyList<BehaviorLearningRound>> Runs)
    {
        public double Hit(string row, int round) => Rate(row, round, trip => trip.LikedStops);

        public double Hate(string row, int round) => Rate(row, round, trip => trip.HatedStops);

        /// <summary>Stops the travelers had to remove over all trips: the effort the ranking left to them.</summary>
        public int Removals(string row) => Travelers.Sum(traveler => Runs[(traveler.Id, row)].Sum(trip => trip.HatedStops));

        public int LikedStops(string row) => Travelers.Sum(traveler => Runs[(traveler.Id, row)].Sum(trip => trip.LikedStops));

        private double Rate(string row, int round, Func<BehaviorLearningRound, int> pick) =>
            Travelers.Sum(traveler => pick(Runs[(traveler.Id, row)][round - 1]))
            / (double)Math.Max(1, Travelers.Sum(traveler => Runs[(traveler.Id, row)][round - 1].OptionalStops));
    }

    public static async Task<(string Report, bool Passed)> RunAsync(string root, string endpoint, int rounds, string runDate)
    {
        var configuration = WeightedGateConfiguration.LoadShipped(root);
        // The same configuration without decision D8, to show what each kind of learning does on its own.
        var withoutCooldown = WeightedGateConfiguration.LoadShipped(root, new Dictionary<string, string?>
        {
            [$"{PersonalizationRankingOptions.SectionName}:{nameof(PersonalizationRankingOptions.RemovedStopCooldownDays)}"] = "0",
        });
        Row[] rows =
        [
            new("A", "A. Rules only", BehaviorLearningMode.RulesOnly, configuration),
            new("A1", "A1. Rules + behavior counting", BehaviorLearningMode.RulesWithCounting, withoutCooldown),
            new(RulesWithCooldown, "A2. Rules + counting + removed-stop cooldown", BehaviorLearningMode.RulesWithCounting, configuration),
            new(AiOnly, "B. AI, no behavior", BehaviorLearningMode.AiOnly, configuration),
            new("C1", "C1. AI + behavior profile", BehaviorLearningMode.AiWithBehavior, withoutCooldown),
            new(Shipped, "**C. AI + behavior profile + removed-stop cooldown (shipped)**", BehaviorLearningMode.AiWithBehavior, configuration),
        ];
        var travelers = BehaviorLearningSimulation.DefaultTravelers();

        using var http = new HttpClient { Timeout = configuration.Ranking.ProviderTimeout };
        var provider = new TripMateAiPoiRankingProvider(http, Options.Create(new PoiRankingProviderOptions
        {
            Enabled = true,
            Provider = AiProviderKind.TripMateAi,
            Endpoint = endpoint,
            ApiKey = Environment.GetEnvironmentVariable("TRIPMATE_AI_API_KEY"),
            ModelName = "bge-m3",
        }));

        async Task<IReadOnlyDictionary<long, decimal>> Rank(PoiRankingRequest request, CancellationToken cancellationToken)
        {
            var result = await provider.RankAsync(request, cancellationToken);
            return result.IsSuccess
                ? result.Value.Ranked.ToDictionary(item => item.PoiId, item => item.AiScore)
                : throw new InvalidOperationException($"AI ranking failed: {result.ErrorCode} {result.ErrorMessage}");
        }

        async Task<Curve> Measure(GoldenScenario scenario)
        {
            // Warm the embedding cache (in two steps: the service embeds within a 4-second budget and keeps each
            // finished batch), so the latency column shows steady-state calls.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    await BehaviorLearningSimulation.RunAsync(
                        scenario, travelers[0], configuration, BehaviorLearningMode.AiWithBehavior, rounds: 2, Rank);
                    break;
                }
                catch (InvalidOperationException) when (attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            var runs = new Dictionary<(string Traveler, string Row), IReadOnlyList<BehaviorLearningRound>>();
            foreach (var traveler in travelers)
            {
                foreach (var row in rows)
                {
                    runs[(traveler.Id, row.Key)] = await BehaviorLearningSimulation.RunAsync(
                        scenario, traveler, row.Configuration, row.Mode, rounds, Rank);
                }
            }

            return new Curve(scenario, travelers, runs);
        }

        Curve real = await Measure(
            SchedulingBenchmarkScenarioFactory.CreateScenario(SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData()));
        Curve city = await Measure(SchedulingBenchmarkScenarioFactory.CreateScenario(LookAlikeCityFactory.Create()));

        bool gainPassed = real.Hit(Shipped, rounds) - real.Hit(Shipped, 1) >= MinimumHitRateGain
            && real.Hate(Shipped, rounds) < real.Hate(Shipped, 1);
        bool firstTripPassed = real.Hit(Shipped, 1) >= real.Hit(AiOnly, 1) - FirstTripTolerance;
        bool passed = gainPassed && firstTripPassed;

        double[] Latencies(Curve curve) => curve.Runs.Where(pair => pair.Key.Row == Shipped)
            .SelectMany(pair => pair.Value)
            .Where(round => round.RankingMilliseconds > 0)
            .Select(round => round.RankingMilliseconds)
            .ToArray();

        var text = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        text.AppendLine("# UC-10 item 6: AI behavior learning curve");
        text.AppendLine();
        text.AppendLine("> Generated by `tools/benchmarks/weighted-gate --behavior-learning`; do not edit by hand.");
        text.AppendLine();
        text.AppendLine(inv, $"- Run date: {runDate}. Endpoint: `{endpoint}` (team AI service, bge-m3) through the production `TripMateAiPoiRankingProvider`.");
        text.AppendLine("- Shipped configuration, `Csp:Mode=WeightedUtility`, 8-hour day. Two data sets:");
        text.AppendLine(inv, $"  - **Real fixture** `{real.Scenario.Id}`: {real.Scenario.Candidates.Count} real Da Nang POIs. The acceptance criteria are judged here.");
        text.AppendLine(inv, $"  - **Simulated city** `{city.Scenario.Id}`: {city.Scenario.Candidates.Count} stops in seven families of look-alike places (8 bridges, 8 museums, 8 pagodas, 8 markets, 8 beaches, 8 hikes, 6 parks), real place names, synthetic positions. As in production, only the {configuration.Generation.EffectiveMaxMatrixCandidates} best-ranked candidates reach the solver.");
        text.AppendLine(inv, $"- {travelers.Count} simulated travelers × {rounds} trips, {BehaviorLearningSimulation.DaysBetweenTrips} day apart (consecutive days of one stay). Each traveler declares a broad interest (or none) and has a **hidden taste** the system never sees.");
        text.AppendLine("- After every trip the traveler removes the stops they hate (Dislike, itinerary edit, weight 1.0) and visits the rest (Like, navigation, weight 0.3). The ranking sees only those signals, through the production weights, 90-day decay, netting and count-based affinity. A fixture's fixed mandatory stop is not counted as a choice.");
        text.AppendLine(inv, $"- **Removed-stop cooldown** (decision D8): a stop removed in the last {configuration.Ranking.RemovedStopCooldownDays} days is not suggested again. All simulated trips fall inside that window. Rows A1 and C1 turn it off to show each kind of learning on its own.");
        text.AppendLine("- **Hit rate**: share of optional stops the traveler truly likes. **Hate rate**: share they would remove. Mandatory stops are excluded.");
        text.AppendLine();
        text.AppendLine("## Acceptance (real fixture)");
        text.AppendLine();
        text.AppendLine("| Criterion | Result | Verdict |");
        text.AppendLine("|---|---|---|");
        text.AppendLine(inv, $"| 1. Learning: hit rate of C rises ≥ {MinimumHitRateGain:P0} from trip 1 to trip {rounds}, and its hate rate falls | hit {real.Hit(Shipped, 1):P0} → {real.Hit(Shipped, rounds):P0}; hate {real.Hate(Shipped, 1):P0} → {real.Hate(Shipped, rounds):P0} | {Verdict(gainPassed)} |");
        text.AppendLine(inv, $"| 2. No regression without history: C on trip 1 is not worse than B | C {real.Hit(Shipped, 1):P0} vs B {real.Hit(AiOnly, 1):P0} | {Verdict(firstTripPassed)} |");
        text.AppendLine(inv, $"| Ranking call with a behavior profile (ms), real fixture | median {SolverComparisonReport.Percentile(Latencies(real), 0.5):F0}, p95 {SolverComparisonReport.Percentile(Latencies(real), 0.95):F0} | info |");
        text.AppendLine(inv, $"| Ranking call with a behavior profile (ms), simulated city | median {SolverComparisonReport.Percentile(Latencies(city), 0.5):F0}, p95 {SolverComparisonReport.Percentile(Latencies(city), 0.95):F0} | info |");
        text.AppendLine();
        text.AppendLine(inv, $"**{(passed ? "PASSED" : "FAILED")}**");

        foreach (var (title, curve) in new[] { ("Real fixture", real), ("Simulated look-alike city", city) })
        {
            text.AppendLine();
            text.AppendLine(inv, $"## Learning curve: {title} ({curve.Scenario.Candidates.Count} POIs, all travelers)");
            text.AppendLine();
            text.AppendLine(inv, $"| Ranking | {string.Join(" | ", Enumerable.Range(1, rounds).Select(round => $"Trip {round} hit / hate"))} | Stops removed over {rounds} trips | Liked stops over {rounds} trips |");
            text.AppendLine(inv, $"|---|{string.Concat(Enumerable.Repeat("---|", rounds))}---|---|");
            foreach (var row in rows)
            {
                text.AppendLine(inv, $"| {row.Label} | {string.Join(" | ", Enumerable.Range(1, rounds).Select(round => $"{curve.Hit(row.Key, round):P0} / {curve.Hate(row.Key, round):P0}"))} | {curve.Removals(row.Key)} | {curve.LikedStops(row.Key)} |");
            }
        }

        text.AppendLine();
        text.AppendLine("## How to read it");
        text.AppendLine();
        text.AppendLine("- A and B cannot learn: every trip is the same day.");
        text.AppendLine("- A1 and C1 have no cooldown: A1 learns by counting (the same POI, or its whole category), C1 through the AI profile, which also moves POIs that resemble what was liked or removed.");
        text.AppendLine("- A2 and C add the cooldown: a removed stop does not come back for 14 days.");
        text.AppendLine("- **Stops removed** is the effort left to the travelers: every hated stop the ranking suggested had to be removed by hand.");
        text.AppendLine();
        text.AppendLine("What the two data sets show:");
        text.AppendLine();
        text.AppendLine(inv, $"- **AI profile against counting, no cooldown** (C1 vs A1), last trip: real fixture hit {real.Hit("C1", rounds):P0} / hate {real.Hate("C1", rounds):P0} vs {real.Hit("A1", rounds):P0} / {real.Hate("A1", rounds):P0}; simulated city {city.Hit("C1", rounds):P0} / {city.Hate("C1", rounds):P0} vs {city.Hit("A1", rounds):P0} / {city.Hate("A1", rounds):P0}.");
        text.AppendLine(inv, $"- **Shipped against rules + cooldown** (C vs A2), last trip: real fixture hit {real.Hit(Shipped, rounds):P0} / hate {real.Hate(Shipped, rounds):P0} vs {real.Hit(RulesWithCooldown, rounds):P0} / {real.Hate(RulesWithCooldown, rounds):P0}; simulated city {city.Hit(Shipped, rounds):P0} / {city.Hate(Shipped, rounds):P0} vs {city.Hit(RulesWithCooldown, rounds):P0} / {city.Hate(RulesWithCooldown, rounds):P0}.");
        text.AppendLine(inv, $"- **Effort over {rounds} trips** (stops removed, C vs A2): real fixture {real.Removals(Shipped)} vs {real.Removals(RulesWithCooldown)}; simulated city {city.Removals(Shipped)} vs {city.Removals(RulesWithCooldown)}.");

        foreach (var (title, curve) in new[] { ("real fixture", real), ("simulated city", city) })
        {
            text.AppendLine();
            text.AppendLine(inv, $"## Per traveler (C, shipped): {title}");
            text.AppendLine();
            text.AppendLine(inv, $"| Traveler | Declared | Hidden likes / hates | {string.Join(" | ", Enumerable.Range(1, rounds).Select(round => $"Trip {round}"))} | Stops |");
            text.AppendLine(inv, $"|---|---|---|{string.Concat(Enumerable.Repeat("---|", rounds))}---|");
            foreach (var traveler in travelers)
            {
                var trips = curve.Runs[(traveler.Id, Shipped)];
                text.AppendLine(inv, $"| {traveler.Id} | {(traveler.DeclaredInterests.Count == 0 ? "(none)" : string.Join(", ", traveler.DeclaredInterests))} | {string.Join(", ", traveler.LikedTags)} / {string.Join(", ", traveler.HatedTags)} | {string.Join(" | ", trips.Select(trip => $"{trip.LikedStops}/{trip.OptionalStops} liked, {trip.HatedStops} hated"))} | {string.Join(" → ", trips.Select(trip => trip.Stops))} |");
            }
        }

        text.AppendLine();
        text.AppendLine("## Plans of the first and last trip (C, shipped, real fixture)");
        text.AppendLine();
        foreach (var traveler in travelers)
        {
            var trips = real.Runs[(traveler.Id, Shipped)];
            text.AppendLine(inv, $"- **{traveler.Id}**: trip 1: {string.Join(" → ", trips[0].StopNames)}. Trip {rounds}: {string.Join(" → ", trips[^1].StopNames)}.");
        }

        return (text.ToString(), passed);
    }

    private static string Verdict(bool passed) => passed ? "PASS" : "FAIL";
}
