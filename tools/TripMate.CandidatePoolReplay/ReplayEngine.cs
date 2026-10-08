using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Enums;

namespace TripMate.CandidatePoolReplay;

public static class ReplayEngine
{
    public const string SchemaVersion = "1.0";
    public const string ToolVersion = "1.0.0";

    public static ReplayOutput Run(string jsonLines, ReplayManifest manifest) =>
        RunCore(jsonLines, manifest, timings: null);

    public static ReplayMeasuredOutput RunMeasured(string jsonLines, ReplayManifest manifest)
    {
        var timings = new ReplayTimingCollector();
        ReplayOutput output = RunCore(jsonLines, manifest, timings);
        return new ReplayMeasuredOutput(output, timings.ToReport());
    }

    private static ReplayOutput RunCore(
        string jsonLines,
        ReplayManifest manifest,
        ReplayTimingCollector? timings)
    {
        ValidateManifest(manifest);
        ReplayScenario[] scenarios = jsonLines
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize(line, ReplayJsonContext.Default.ReplayScenario)
                ?? throw new ReplayValidationException("A scenario line is null."))
            .Select(EnsureRequiredCollections)
            .Select(NormalizeScenario)
            .ToArray();
        if (scenarios.Length == 0)
        {
            throw new ReplayValidationException("The corpus is empty.");
        }

        string? duplicateScenario = scenarios.GroupBy(item => item.ScenarioId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (duplicateScenario is not null)
        {
            throw new ReplayValidationException($"Duplicate scenario ID '{duplicateScenario}'.");
        }

        ScenarioReplayResult[] results = scenarios
            .OrderBy(item => item.ScenarioId, StringComparer.Ordinal)
            .Select(item => RunScenario(item, manifest, timings))
            .ToArray();
        string normalizedCorpus = string.Join('\n', scenarios
            .OrderBy(item => item.ScenarioId, StringComparer.Ordinal)
            .Select(item => JsonSerializer.Serialize(item, ReplayJsonContext.Default.ReplayScenario)));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCorpus)))
            .ToLowerInvariant();
        return new ReplayOutput(
            SchemaVersion,
            ToolVersion,
            Environment.Version.ToString(),
            hash,
            manifest,
            results);
    }

    public static string Serialize(ReplayOutput output) =>
        JsonSerializer.Serialize(output, ReplayJsonContext.Default.ReplayOutput);

    public static string SerializeTiming(ReplayTimingReport timing) =>
        JsonSerializer.Serialize(timing, ReplayJsonContext.Default.ReplayTimingReport);

    private static ScenarioReplayResult RunScenario(
        ReplayScenario scenario,
        ReplayManifest manifest,
        ReplayTimingCollector? timings)
    {
        ValidateScenario(scenario, manifest);
        string[] mandatoryIds = MandatoryIds(scenario);
        Dictionary<string, long> numericIds = mandatoryIds
            .Concat(scenario.Candidates.Select(candidate => candidate.Id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select((id, index) => (id, numeric: checked((long)index + 1)))
            .ToDictionary(item => item.id, item => item.numeric, StringComparer.Ordinal);
        Dictionary<long, string> opaqueIds = numericIds.ToDictionary(pair => pair.Value, pair => pair.Key);

        StrategyReplayResult baseline = RunTopK(
            "A-current",
            scenario,
            manifest.ProviderCap,
            manifest.MatrixCap,
            numericIds,
            opaqueIds,
            backfill: false,
            timings);
        StrategyReplayResult backfill = RunTopK(
            "B-frozen-overflow",
            scenario,
            manifest.ProviderCap,
            manifest.MatrixCap,
            numericIds,
            opaqueIds,
            backfill: true,
            timings);
        StrategyReplayResult diversity = RunDiversity(
            scenario,
            manifest,
            numericIds,
            opaqueIds,
            timings);
        StrategyReplayResult sensitivity = RunTopK(
            "D-cap-sensitivity",
            scenario,
            manifest.SensitivityProviderCap,
            manifest.SensitivityMatrixCap,
            numericIds,
            opaqueIds,
            backfill: false,
            timings);

        StrategyReplayResult[] strategies = [baseline, backfill, diversity, sensitivity];
        strategies = strategies
            .Select(item => item with { PairedDelta = Delta(item, baseline) })
            .ToArray();
        return new ScenarioReplayResult(
            scenario.ScenarioId,
            scenario.Candidates.Count,
            mandatoryIds.Length,
            scenario.TransportMode,
            scenario.DensityBucket,
            strategies);
    }

    private static T Measure<T>(
        ReplayTimingCollector? timings,
        string strategy,
        string stage,
        Func<T> action) =>
        timings is null ? action() : timings.Measure(strategy, stage, action);

    private static StrategyReplayResult RunTopK(
        string strategy,
        ReplayScenario scenario,
        int providerCap,
        int matrixCap,
        IReadOnlyDictionary<string, long> numericIds,
        IReadOnlyDictionary<long, string> opaqueIds,
        bool backfill,
        ReplayTimingCollector? timings)
    {
        TopKSelection selection = Measure(timings, strategy, "selection", () =>
        {
            ProviderPoolSelection provider = ProviderPoolSelector.Select(
                scenario.Candidates
                    .Select(candidate => ToProviderCandidate(candidate, numericIds[candidate.Id]))
                    .ToArray(),
                providerCap);
            ReplayCandidate[] providerCandidates = provider.Candidates
                .Select(item => Find(scenario, opaqueIds[item.Candidate.PoiId]))
                .ToArray();
            bool providerComparable = providerCandidates.All(candidate => candidate.EffectiveScore.HasValue);
            MatrixCandidateSelection matrix = MatrixCandidateSelector.Select(
                MandatoryIds(scenario).Select(id => numericIds[id]).ToArray(),
                providerCandidates.Select(candidate => ToSnapshot(
                    candidate,
                    numericIds[candidate.Id],
                    providerComparable)).ToArray(),
                matrixCap);
            string[] matrixOptional = matrix.OrderedPoiIds
                .Select(id => opaqueIds[id])
                .Where(id => !MandatoryIds(scenario).Contains(id, StringComparer.Ordinal))
                .ToArray();
            var selected = matrixOptional
                .Where(id => Find(scenario, id).ValidAtFinalize)
                .ToList();
            if (backfill)
            {
                foreach (ReplayCandidate overflow in providerCandidates
                    .Where(candidate => !matrixOptional.Contains(candidate.Id, StringComparer.Ordinal))
                    .Where(candidate => candidate.ValidAtFinalize))
                {
                    if (selected.Count >= matrix.OptionalCapacity)
                    {
                        break;
                    }

                    selected.Add(overflow.Id);
                }
            }

            IReadOnlyList<string> preparedMatrixIds = backfill
                ? selected
                : matrixOptional;
            return new TopKSelection(
                providerCandidates,
                preparedMatrixIds,
                selected,
                providerComparable);
        });

        return Summarize(
            strategy,
            scenario,
            selection.ProviderCandidates.Select(item => item.Id),
            selection.PreparedMatrixIds,
            selection.SelectedIds,
            numericIds,
            selection.ProviderComparable,
            timings);
    }

    private static StrategyReplayResult RunDiversity(
        ReplayScenario scenario,
        ReplayManifest manifest,
        IReadOnlyDictionary<string, long> numericIds,
        IReadOnlyDictionary<long, string> opaqueIds,
        ReplayTimingCollector? timings)
    {
        DiversitySelection selection = Measure(
            timings,
            "C-desirability-diversity",
            "selection",
            () =>
            {
                ProviderPoolSelection provider = ProviderPoolSelector.Select(
                    scenario.Candidates.Select(candidate =>
                        ToProviderCandidate(candidate, numericIds[candidate.Id])).ToArray(),
                    manifest.ProviderCap);
                ReplayCandidate[] frozenPool = provider.Candidates
                    .Select(item => Find(scenario, opaqueIds[item.Candidate.PoiId]))
                    .ToArray();
                bool providerComparable = frozenPool.All(candidate => candidate.EffectiveScore.HasValue);
                string[] desirabilityOrder = MatrixCandidateSelector.Select(
                [],
                frozenPool.Select(candidate => ToSnapshot(
                    candidate,
                    numericIds[candidate.Id],
                    providerComparable)).ToArray(),
                frozenPool.Length)
                    .OrderedPoiIds.Select(id => opaqueIds[id])
                    .ToArray();
                ReplayCandidate[] pool = desirabilityOrder
                    .Select(id => Find(scenario, id))
                    .Where(item => item.ValidAtFinalize)
                    .ToArray();
                int capacity = Math.Max(0, manifest.MatrixCap - MandatoryIds(scenario).Length);
                int coreCount = Math.Min(Math.Min(manifest.DiversityCoreSize, capacity), pool.Length);
                var selected = pool.Take(coreCount).ToList();
                int diversityLimit = Math.Min(capacity, coreCount + manifest.DiversityReservedSlots);
                while (selected.Count < diversityLimit)
                {
                    HashSet<string> categories = selected.Select(item => item.Category).ToHashSet(StringComparer.Ordinal);
                    HashSet<string> cells = selected.Select(item => item.GeoCell).ToHashSet(StringComparer.Ordinal);
                    ReplayCandidate? next = pool
                        .Where(item => !selected.Contains(item))
                        .OrderByDescending(item =>
                            (categories.Contains(item.Category) ? 0 : manifest.CategoryDiversityWeight)
                            + (cells.Contains(item.GeoCell) ? 0 : manifest.GeoDiversityWeight))
                        .ThenByDescending(item => providerComparable
                            ? item.EffectiveScore!.Value
                            : item.BaseScore)
                        .ThenBy(item => item.Id, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (next is null)
                    {
                        break;
                    }

                    selected.Add(next);
                }

                selected.AddRange(pool
                    .Where(item => !selected.Contains(item))
                    .Take(capacity - selected.Count));
                return new DiversitySelection(frozenPool, selected, providerComparable);
            });
        return Summarize(
            "C-desirability-diversity",
            scenario,
            selection.FrozenPool.Select(item => item.Id),
            selection.Selected.Select(item => item.Id),
            selection.Selected.Select(item => item.Id),
            numericIds,
            selection.ProviderComparable,
            timings);
    }

    private static StrategyReplayResult Summarize(
        string strategy,
        ReplayScenario scenario,
        IEnumerable<string> providerIds,
        IEnumerable<string> matrixIds,
        IEnumerable<string> generationIds,
        IReadOnlyDictionary<string, long> numericIds,
        bool providerComparable,
        ReplayTimingCollector? timings)
    {
        string[] provider = providerIds.ToArray();
        string[] matrix = matrixIds.ToArray();
        string[] generation = generationIds.ToArray();
        GeneratedReplayPlan generated = Measure(
            timings,
            strategy,
            "generation",
            () => GeneratePlan(
                scenario,
                generation,
                numericIds,
                providerComparable));
        string[] planned = generated.OptionalVisitIds;
        ReplayCandidate[] candidates = planned.Select(id => Find(scenario, id)).ToArray();
        int points = MandatoryIds(scenario).Length + matrix.Length + 2;
        return new StrategyReplayResult(
            strategy,
            providerComparable ? "provider-comparable" : "base-only",
            provider,
            matrix,
            planned,
            candidates.Sum(item => item.BaseScore),
            candidates.Length == 0 ? 0m : candidates.Average(item => item.BaseScore),
            providerComparable ? candidates.Sum(item => item.EffectiveScore!.Value) : null,
            providerComparable
                ? candidates.Length == 0
                    ? 0m
                    : candidates.Average(item => item.EffectiveScore!.Value)
                : null,
            candidates.Select(item => item.Category).Distinct(StringComparer.Ordinal).Count(),
            candidates.Select(item => item.GeoCell).Distinct(StringComparer.Ordinal).Count(),
            generated.TravelMinutes,
            generated.Feasible,
            points,
            checked(points * points),
            null);
    }

    private static PairedStrategyDelta Delta(
        StrategyReplayResult strategy,
        StrategyReplayResult baseline)
    {
        HashSet<string> strategySet = strategy.PlannedOptional.ToHashSet(StringComparer.Ordinal);
        HashSet<string> baselineSet = baseline.PlannedOptional.ToHashSet(StringComparer.Ordinal);
        return new PairedStrategyDelta(
            strategy.BaseUtilitySum - baseline.BaseUtilitySum,
            strategy.BaseUtilityMean - baseline.BaseUtilityMean,
            strategy.EffectiveUtilitySum.HasValue && baseline.EffectiveUtilitySum.HasValue
                ? strategy.EffectiveUtilitySum.Value - baseline.EffectiveUtilitySum.Value
                : null,
            strategy.EffectiveUtilityMean.HasValue && baseline.EffectiveUtilityMean.HasValue
                ? strategy.EffectiveUtilityMean.Value - baseline.EffectiveUtilityMean.Value
                : null,
            strategy.CategoryCoverage - baseline.CategoryCoverage,
            strategy.GeoCoverage - baseline.GeoCoverage,
            strategy.TravelMinutes - baseline.TravelMinutes,
            strategy.MatrixPoints - baseline.MatrixPoints,
            strategy.MatrixElements - baseline.MatrixElements,
            !strategySet.SetEquals(baselineSet),
            !strategy.PlannedOptional.SequenceEqual(
                baseline.PlannedOptional,
                StringComparer.Ordinal));
    }

    private static GeneratedReplayPlan GeneratePlan(
        ReplayScenario scenario,
        IReadOnlyList<string> matrixOptionalIds,
        IReadOnlyDictionary<string, long> numericIds,
        bool providerComparable)
    {
        string[] mandatoryIds = MandatoryIds(scenario);
        string[] candidateIds = [.. mandatoryIds, .. matrixOptionalIds];
        Dictionary<string, int> sourceIndexes = scenario.MatrixOrder
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        string[] subsetOrder = ["start", .. candidateIds, "end"];
        var minutes = new int[subsetOrder.Length, subsetOrder.Length];
        for (var row = 0; row < subsetOrder.Length; row++)
        {
            for (var column = 0; column < subsetOrder.Length; column++)
            {
                minutes[row, column] = scenario.RouteMinutes[sourceIndexes[subsetOrder[row]]][sourceIndexes[subsetOrder[column]]];
            }
        }

        GenerationCandidate[] candidates =
        [
            .. scenario.MandatoryCandidates.Select(item => new GenerationCandidate(
                numericIds[item.Id],
                item.Id,
                new RoutePoint(0, 0),
                item.VisitDurationMinutes,
                item.Cost,
                item.OpeningHours.Select(ToOpeningHours).ToArray(),
                0,
                0,
                null,
                null,
                item.Cost,
                CategoryName: item.Category)),
            .. matrixOptionalIds.Select(id => Find(scenario, id)).Select(item => new GenerationCandidate(
                numericIds[item.Id],
                item.Id,
                new RoutePoint(0, 0),
                item.VisitDurationMinutes,
                item.Cost,
                item.OpeningHours.Select(ToOpeningHours).ToArray(),
                item.BaseScore,
                providerComparable ? item.EffectiveScore!.Value : item.BaseScore,
                item.ScenicScore,
                item.PhotoRating,
                item.Cost,
                CategoryName: item.Category)),
        ];
        var input = new GenerationInput(
            scenario.StartAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById(scenario.TimeZoneId),
            new RoutePoint(0, 0),
            new RoutePoint(0, 0),
            scenario.AvailableMinutes,
            ParseTransportMode(scenario.TransportMode),
            ParseRestPreference(scenario.RestPreference),
            scenario.BudgetVnd,
            candidates,
            mandatoryIds.Select(id => numericIds[id]).ToArray());
        var service = new ItineraryGenerationService(
            new FrozenReplayRouteDurationProvider(RouteDurationMatrix.Create(minutes)));
        var result = service.GenerateAsync(input, CancellationToken.None).GetAwaiter().GetResult();
        if (result.IsFailure)
        {
            return new GeneratedReplayPlan(false, [], 0);
        }

        Dictionary<long, string> opaqueIds = numericIds.ToDictionary(pair => pair.Value, pair => pair.Key);
        string[] orderedVisits = result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit
                && item.PointOfInterestId.HasValue)
            .Select(item => opaqueIds[item.PointOfInterestId!.Value])
            .ToArray();
        string[] optionalVisits = orderedVisits
            .Where(id => !mandatoryIds.Contains(id, StringComparer.Ordinal))
            .ToArray();
        int travelMinutes = CalculateTravelMinutes(scenario, orderedVisits);
        return new GeneratedReplayPlan(true, optionalVisits, travelMinutes);
    }

    private static int CalculateTravelMinutes(
        ReplayScenario scenario,
        IReadOnlyList<string> orderedVisitIds)
    {
        Dictionary<string, int> indexes = scenario.MatrixOrder
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        string[] route = ["start", .. orderedVisitIds, "end"];
        var total = 0;
        for (var index = 0; index < route.Length - 1; index++)
        {
            total = checked(total
                + scenario.RouteMinutes[indexes[route[index]]][indexes[route[index + 1]]]);
        }

        return total;
    }

    private static GenerationOpeningHours ToOpeningHours(ReplayOpeningHours item) =>
        new(item.DayOfWeek, item.OpenTime, item.CloseTime);

    private static TransportMode ParseTransportMode(string value) => value switch
    {
        "walking" => TransportMode.Walking,
        "motorbike" => TransportMode.Motorbike,
        "car" => TransportMode.Car,
        "public_transit" => TransportMode.PublicTransit,
        _ => throw new ReplayValidationException($"Unsupported transport mode '{value}'."),
    };

    private static RestPreference ParseRestPreference(string value) => value switch
    {
        "auto" => RestPreference.Auto,
        "none" => RestPreference.None,
        "frequent" => RestPreference.Frequent,
        _ => throw new ReplayValidationException($"Unsupported rest preference '{value}'."),
    };

    private static ProviderPoolSelectionCandidate ToProviderCandidate(ReplayCandidate item, long id) =>
        new(
            new PoiRankingInputCandidate(
                id,
                0,
                string.Empty,
                string.Empty,
                [],
                item.ScenicScore,
                item.PhotoRating,
                item.Distance,
                item.Cost),
            item.BaseScore);

    private static PoiRankingSnapshotEntry ToSnapshot(
        ReplayCandidate item,
        long id,
        bool providerComparable) =>
        new(
            id,
            item.BaseScore,
            providerComparable ? item.EffectiveScore!.Value : item.BaseScore,
            item.ScenicScore,
            item.PhotoRating,
            item.Distance,
            item.Cost);

    private static ReplayCandidate Find(ReplayScenario scenario, string id) =>
        scenario.Candidates.Single(item => item.Id == id);

    private static ReplayScenario NormalizeScenario(ReplayScenario scenario) => scenario with
    {
        MandatoryCandidates = scenario.MandatoryCandidates.OrderBy(
            item => item.Id,
            StringComparer.Ordinal).ToArray(),
        Candidates = scenario.Candidates.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
    };

    private static string[] MandatoryIds(ReplayScenario scenario) =>
        scenario.MandatoryCandidates.Select(item => item.Id).ToArray();

    private static ReplayScenario EnsureRequiredCollections(ReplayScenario scenario)
    {
        if (scenario.MandatoryCandidates is null
            || scenario.Candidates is null
            || scenario.MatrixOrder is null
            || scenario.RouteMinutes is null)
        {
            throw new ReplayValidationException(
                $"Scenario '{scenario.ScenarioId ?? "<missing>"}' has a missing required collection.");
        }

        return scenario;
    }

    private static void ValidateManifest(ReplayManifest manifest)
    {
        if (manifest.SchemaVersion != SchemaVersion)
        {
            throw new ReplayValidationException($"Unsupported manifest schema '{manifest.SchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.CodeVersion))
        {
            throw new ReplayValidationException("Manifest code version is required.");
        }

        if (manifest.Purpose is not ("correctness" or "exploratory" or "inferential"))
        {
            throw new ReplayValidationException("Manifest purpose must be correctness, exploratory, or inferential.");
        }

        if (manifest.ProviderCap < 0 || manifest.MatrixCap < 0
            || manifest.DiversityCoreSize < 0 || manifest.DiversityReservedSlots < 0
            || manifest.CategoryDiversityWeight < 0 || manifest.GeoDiversityWeight < 0
            || string.IsNullOrWhiteSpace(manifest.GeoCellDefinition)
            || manifest.BackfillOrder != "frozen-provider-order"
            || manifest.SensitivityProviderCap < manifest.ProviderCap
            || manifest.SensitivityMatrixCap < manifest.MatrixCap)
        {
            throw new ReplayValidationException("Manifest cap and diversity options are inconsistent.");
        }
    }

    private static void ValidateScenario(ReplayScenario scenario, ReplayManifest manifest)
    {
        if (scenario.SchemaVersion != SchemaVersion)
        {
            throw new ReplayValidationException($"Unsupported scenario schema '{scenario.SchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(scenario.ScenarioId))
        {
            throw new ReplayValidationException("Scenario ID is required.");
        }

        if (scenario.TransportMode is not ("walking" or "motorbike" or "car" or "public_transit"))
        {
            throw new ReplayValidationException(
                $"Scenario '{scenario.ScenarioId}' has an unsupported transport mode.");
        }

        if (scenario.RestPreference is not ("auto" or "none" or "frequent")
            || string.IsNullOrWhiteSpace(scenario.TimeZoneId)
            || scenario.AvailableMinutes is < 60 or > 720
            || scenario.BudgetVnd is <= 0)
        {
            throw new ReplayValidationException(
                $"Scenario '{scenario.ScenarioId}' has inconsistent scheduling options.");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(scenario.TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException
            or InvalidTimeZoneException)
        {
            throw new ReplayValidationException(
                $"Scenario '{scenario.ScenarioId}' has an unknown time zone: {exception.Message}");
        }

        string[] mandatoryIds = MandatoryIds(scenario);
        string? duplicate = mandatoryIds.Concat(scenario.Candidates.Select(item => item.Id))
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (duplicate is not null)
        {
            throw new ReplayValidationException($"Scenario '{scenario.ScenarioId}' has duplicate candidate ID '{duplicate}'.");
        }

        string[] expectedOrder = ["start", .. mandatoryIds, .. scenario.Candidates.Select(item => item.Id), "end"];
        if (scenario.MatrixOrder.Count != expectedOrder.Length
            || expectedOrder.Except(scenario.MatrixOrder, StringComparer.Ordinal).Any()
            || scenario.MatrixOrder.Distinct(StringComparer.Ordinal).Count() != scenario.MatrixOrder.Count)
        {
            throw new ReplayValidationException($"Scenario '{scenario.ScenarioId}' matrix order is incomplete or inconsistent.");
        }

        if (scenario.RouteMinutes.Count != scenario.MatrixOrder.Count
            || scenario.RouteMinutes.Any(row => row.Count != scenario.MatrixOrder.Count)
            || scenario.RouteMinutes.SelectMany(row => row).Any(value => value < 0))
        {
            throw new ReplayValidationException($"Scenario '{scenario.ScenarioId}' route matrix must be complete, square, and non-negative.");
        }

        if (mandatoryIds.Length > 6
            || mandatoryIds.Length > manifest.MatrixCap)
        {
            throw new ReplayValidationException($"Scenario '{scenario.ScenarioId}' has too many mandatory candidates.");
        }

        if (scenario.MandatoryCandidates.Any(item => item.VisitDurationMinutes <= 0
                || item.OpeningHours.Count == 0)
            || scenario.Candidates.Any(item => item.VisitDurationMinutes <= 0
                || item.OpeningHours.Count == 0
                || string.IsNullOrWhiteSpace(item.Category)
                || string.IsNullOrWhiteSpace(item.GeoCell)))
        {
            throw new ReplayValidationException(
                $"Scenario '{scenario.ScenarioId}' has incomplete planning fields.");
        }
    }

    private sealed record GeneratedReplayPlan(
        bool Feasible,
        string[] OptionalVisitIds,
        int TravelMinutes);

    private sealed record TopKSelection(
        IReadOnlyList<ReplayCandidate> ProviderCandidates,
        IReadOnlyList<string> PreparedMatrixIds,
        IReadOnlyList<string> SelectedIds,
        bool ProviderComparable);

    private sealed record DiversitySelection(
        IReadOnlyList<ReplayCandidate> FrozenPool,
        IReadOnlyList<ReplayCandidate> Selected,
        bool ProviderComparable);

    private sealed class ReplayTimingCollector
    {
        private readonly Dictionary<(string Strategy, string Stage), List<TimingSample>> _samples = [];

        public T Measure<T>(
            string strategy,
            string stage,
            Func<T> action)
        {
            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            long startedAt = Stopwatch.GetTimestamp();
            T result = action();
            double elapsedMicroseconds = Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds;
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            var key = (strategy, stage);
            if (!_samples.TryGetValue(key, out List<TimingSample>? samples))
            {
                samples = [];
                _samples.Add(key, samples);
            }

            samples.Add(new TimingSample(elapsedMicroseconds, allocatedBytes));
            return result;
        }

        public ReplayTimingReport ToReport() => new(
            DateTimeOffset.UtcNow,
            Environment.Version.ToString(),
            RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            _samples
                .OrderBy(pair => pair.Key.Strategy, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Stage, StringComparer.Ordinal)
                .Select(pair => SummarizeTiming(
                    pair.Key.Strategy,
                    pair.Key.Stage,
                    pair.Value))
                .ToArray());

        private static StrategyTimingSummary SummarizeTiming(
            string strategy,
            string stage,
            IReadOnlyCollection<TimingSample> samples)
        {
            double[] elapsed = samples
                .Select(sample => sample.ElapsedMicroseconds)
                .Order()
                .ToArray();
            return new StrategyTimingSummary(
                strategy,
                stage,
                elapsed.Length,
                Percentile(elapsed, 0.50),
                Percentile(elapsed, 0.95),
                Percentile(elapsed, 0.99),
                samples.Average(sample => (double)sample.AllocatedBytes));
        }

        private static double Percentile(IReadOnlyList<double> sorted, double percentile) =>
            sorted[Math.Clamp(
                (int)Math.Ceiling(sorted.Count * percentile) - 1,
                0,
                sorted.Count - 1)];

        private sealed record TimingSample(
            double ElapsedMicroseconds,
            long AllocatedBytes);
    }

    private sealed class FrozenReplayRouteDurationProvider(RouteDurationMatrix matrix)
        : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (points.Count != matrix.PointCount)
            {
                throw new ReplayValidationException("Frozen route matrix point count is inconsistent.");
            }

            return Task.FromResult(matrix);
        }
    }
}