using System.Diagnostics;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiRanking;
using TripMate.Infrastructure.TripMateAi;

/// <summary>
/// UC-10 AI level 1 (2026-11-11): the same Da Nang day and personas, scheduled with AI ranking off (fallback score) and on
/// (the team AI service, bge-m3, through the production <see cref="TripMateAiPoiRankingProvider"/>).
/// </summary>
internal static class AiRankingComparison
{
    private static readonly TravelerPace[] Paces = [TravelerPace.Relaxed, TravelerPace.Moderate, TravelerPace.Fast];

    public static async Task<string> RunAsync(string root, string endpoint, string runDate)
    {
        var configuration = WeightedGateConfiguration.LoadShipped(root);
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(cityData);
        var personas = cityData.Personas
            .Where(persona => SchedulingBenchmarkScenarioFactory.CreateProfile(persona, cityData.StartAtUtc) is not null)
            .ToArray();

        using var http = new HttpClient { Timeout = configuration.Ranking.ProviderTimeout };
        var provider = new TripMateAiPoiRankingProvider(http, Options.Create(new PoiRankingProviderOptions
        {
            Enabled = true,
            Provider = AiProviderKind.TripMateAi,
            Endpoint = endpoint,
            ApiKey = Environment.GetEnvironmentVariable("TRIPMATE_AI_API_KEY"),
            ModelName = "bge-m3",
        }));

        var latencies = new List<double>();
        var scoresByPersona = new Dictionary<string, IReadOnlyDictionary<long, decimal>>();
        foreach (var persona in personas)
        {
            var request = new PoiRankingRequest(
                scenario.Candidates
                    .Select(c => new PoiRankingCandidate(c.Id, c.Name, c.CategoryName ?? string.Empty, c.Tags?.ToArray() ?? []))
                    .ToArray(),
                new PoiRankingContext(persona.Tags.Select(WeightedGateExecutor.NormalizeInterest).ToArray()));
            var clock = Stopwatch.StartNew();
            var result = await provider.RankAsync(request, CancellationToken.None);
            clock.Stop();
            latencies.Add(clock.Elapsed.TotalMilliseconds);
            if (result.IsFailure)
            {
                throw new InvalidOperationException($"AI ranking failed for {persona.Id}: {result.ErrorCode} {result.ErrorMessage}");
            }

            scoresByPersona[persona.Id] = result.Value.Ranked.ToDictionary(item => item.PoiId, item => item.AiScore);
        }

        var rows = new List<(string Persona, TravelerPace Pace, Plan Off, Plan On)>();
        var offRuns = new List<WeightedGateRun>();
        var onRuns = new List<WeightedGateRun>();
        foreach (var persona in personas)
        {
            foreach (var pace in Paces)
            {
                var paced = persona with { Pace = pace };
                var off = await WeightedGateExecutor.RunAsync(scenario, paced, configuration, 0, 1);
                var on = await WeightedGateExecutor.RunAsync(scenario, paced, configuration, 0, 1, aiScores: scoresByPersona[persona.Id]);
                offRuns.Add(off);
                onRuns.Add(on);
                rows.Add((persona.Id, pace, Plan.Of(off, scenario, persona), Plan.Of(on, scenario, persona)));
            }
        }

        var offOverlap = WeightedGateSummary.Create([], offRuns).OverlapByPace;
        var onOverlap = WeightedGateSummary.Create([], onRuns).OverlapByPace;
        return Render(runDate, endpoint, configuration, latencies, scoresByPersona, scenario, rows, offOverlap, onOverlap);
    }

    /// <summary>One schedule's interest match, measured by the rule-based interest tokens only (neutral to AI).</summary>
    private sealed record Plan(int Stops, int OptionalStops, int OptionalInterestHits, IReadOnlyList<string> Names)
    {
        public double HitRate => OptionalStops == 0 ? 0 : (double)OptionalInterestHits / OptionalStops;

        public static Plan Of(WeightedGateRun run, GoldenScenario scenario, BenchmarkPersonaData persona)
        {
            var interests = persona.Tags.Select(WeightedGateExecutor.NormalizeInterest).ToHashSet();
            var byId = scenario.Candidates.ToDictionary(c => c.Id);
            var optional = run.OptionalVisitPoiIds.Select(id => byId[id]).ToArray();
            var hits = optional.Count(c =>
                interests.Contains(WeightedGateExecutor.NormalizeInterest(c.CategoryName ?? string.Empty))
                || (c.Tags ?? []).Any(tag => interests.Contains(WeightedGateExecutor.NormalizeInterest(tag))));
            return new Plan(run.StopCount, optional.Length, hits, run.VisitPoiIds.Select(id => byId[id].Name).ToArray());
        }
    }

    private static string Render(
        string runDate,
        string endpoint,
        WeightedGateConfiguration configuration,
        List<double> latencies,
        Dictionary<string, IReadOnlyDictionary<long, decimal>> scores,
        GoldenScenario scenario,
        List<(string Persona, TravelerPace Pace, Plan Off, Plan On)> rows,
        IReadOnlyList<WeightedGatePaceOverlap> offOverlap,
        IReadOnlyList<WeightedGatePaceOverlap> onOverlap)
    {
        var text = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        text.AppendLine("# UC-10 AI ranking on vs off (AI level 1)");
        text.AppendLine();
        text.AppendLine("> Generated by `tools/benchmarks/weighted-gate --ai-ranking`; do not edit by hand.");
        text.AppendLine();
        text.AppendLine(inv, $"- Run date: {runDate}. Endpoint: `{endpoint}` (team AI service, bge-m3) through the production `TripMateAiPoiRankingProvider`.");
        text.AppendLine(inv, $"- Shipped configuration, `Csp:Mode=WeightedUtility`. Blend: {configuration.Ranking.BaseWeight} × base + {configuration.Ranking.AiWeight} × AI; AI off uses the fallback {configuration.Ranking.PersonalizedAiFallbackScore}.");
        text.AppendLine(inv, $"- Scenario: {scenario.Id}, {scenario.Candidates.Count} candidates, 8-hour day; personas with a profile × 3 paces.");
        text.AppendLine(inv, $"- AI call latency per persona (ms): {string.Join(", ", latencies.Select(ms => ms.ToString("F0", inv)))}. The first call includes the model warm-up.");
        text.AppendLine("- **Interest hits** count optional stops whose category or tags match the persona's declared interests (rule-based, so neutral to AI).");
        text.AppendLine();
        text.AppendLine("## Summary");
        text.AppendLine();
        text.AppendLine("| Pace | Interest hit rate off → on | Mean stops off → on | Persona overlap (Jaccard) off → on |");
        text.AppendLine("|---|---|---|---|");
        foreach (var pace in Paces)
        {
            var group = rows.Where(row => row.Pace == pace).ToArray();
            double Rate(Func<(string, TravelerPace, Plan Off, Plan On), Plan> pick) =>
                group.Sum(row => pick(row).OptionalInterestHits) / (double)Math.Max(1, group.Sum(row => pick(row).OptionalStops));
            var off = offOverlap.Single(o => o.Pace == pace).MeanJaccard;
            var on = onOverlap.Single(o => o.Pace == pace).MeanJaccard;
            text.AppendLine(inv, $"| {pace} | {Rate(r => r.Off):P0} → {Rate(r => r.On):P0} | {group.Average(r => r.Off.Stops):F1} → {group.Average(r => r.On.Stops):F1} | {off:F2} → {on:F2} |");
        }

        text.AppendLine();
        text.AppendLine("## Plans");
        text.AppendLine();
        text.AppendLine("| Persona | Pace | AI off | AI on | Hits off → on |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var row in rows)
        {
            var same = row.Off.Names.SequenceEqual(row.On.Names);
            text.AppendLine(inv, $"| {row.Persona} | {row.Pace} | {string.Join(" → ", row.Off.Names)} | {(same ? "(same)" : string.Join(" → ", row.On.Names))} | {row.Off.OptionalInterestHits}/{row.Off.OptionalStops} → {row.On.OptionalInterestHits}/{row.On.OptionalStops} |");
        }

        text.AppendLine();
        text.AppendLine("## AI scores (top 5 per persona)");
        text.AppendLine();
        var names = scenario.Candidates.ToDictionary(c => c.Id, c => $"{c.Name} ({c.CategoryName})");
        foreach (var (persona, map) in scores)
        {
            text.AppendLine(inv, $"- **{persona}**: {string.Join("; ", map.OrderByDescending(p => p.Value).Take(5).Select(p => $"{names[p.Key]} {p.Value:F2}"))}");
        }

        return text.ToString();
    }
}
