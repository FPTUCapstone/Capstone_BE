using System.Collections.Frozen;
using System.Diagnostics;

using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Scheduling.Personalization;

internal sealed record PoiRankingInput(
    long TravelerUserId,
    IReadOnlyCollection<string> PreferenceTokens,
    IReadOnlyCollection<PoiRankingInputCandidate> Candidates);

internal sealed record PoiRankingInputCandidate(
    long PoiId,
    int CategoryId,
    string Name,
    string CategoryName,
    IReadOnlyCollection<string> TagNames,
    decimal? ScenicScore,
    decimal? PhotoRating,
    decimal ExplorationDistanceForRanking,
    decimal? EstimatedVisitCostForRanking);

internal sealed record ValidatedPoiRankingItem(
    long PoiId,
    decimal AiScore,
    string? Reason);

public sealed class PoiRankingOrchestrator
{
    private const int MaximumReasonLength = 500;

    private readonly PersonalBehaviorFeatureAggregator _behaviorAggregator;
    private readonly IPoiRankingProvider _provider;
    private readonly PersonalizationRankingOptions _options;
    private readonly bool _providerEnabled;
    private readonly ILogger<PoiRankingOrchestrator> _logger;
    private readonly PersonalBehaviorAffinityScorer _behaviorScorer;
    private readonly PersonalizationBaseScorer _baseScorer;

    public PoiRankingOrchestrator(
        PersonalBehaviorFeatureAggregator behaviorAggregator,
        IPoiRankingProvider provider,
        PersonalizationRankingOptions options,
        bool providerEnabled,
        ILogger<PoiRankingOrchestrator> logger)
    {
        _behaviorAggregator = behaviorAggregator
            ?? throw new ArgumentNullException(nameof(behaviorAggregator));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _providerEnabled = providerEnabled;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _behaviorScorer = new PersonalBehaviorAffinityScorer(options);
        _baseScorer = new PersonalizationBaseScorer(options);
    }

    internal async Task<PoiRankingSnapshot> BuildRankingAsync(
        PoiRankingInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        string[] preferenceTokens = input.PreferenceTokens.ToArray();
        PoiRankingInputCandidate[] candidates = input.Candidates
            .Select(candidate => candidate with { TagNames = candidate.TagNames.ToArray() })
            .ToArray();

        if (candidates.Length == 0)
        {
            LogOutcome(
                LogLevel.Information,
                candidateCount: 0,
                poolCount: 0,
                latencyMilliseconds: 0,
                outcome: "provider-skipped",
                fallbackUsed: false,
                timedOut: false);
            return EmptySnapshot();
        }

        PersonalBehaviorAggregation aggregation = await _behaviorAggregator.AggregateAsync(
            input.TravelerUserId,
            candidates.Select(candidate => candidate.PoiId).Distinct().ToArray(),
            candidates.Select(candidate => candidate.CategoryId).Distinct().ToArray(),
            cancellationToken);
        ScoredCandidate[] providerPool = candidates
            .Select(candidate => ScoreCandidate(candidate, preferenceTokens, aggregation))
            .OrderByDescending(candidate => candidate.BaseScore)
            .ThenByDescending(candidate => candidate.Candidate.ScenicScore ?? decimal.MinValue)
            .ThenByDescending(candidate => candidate.Candidate.PhotoRating ?? decimal.MinValue)
            .ThenBy(candidate => candidate.Candidate.ExplorationDistanceForRanking)
            .ThenBy(candidate =>
                candidate.Candidate.EstimatedVisitCostForRanking ?? decimal.MaxValue)
            .ThenBy(candidate => candidate.Candidate.PoiId)
            .Take(_options.MaxProviderCandidates)
            .ToArray();
        FrozenSet<long> providerPoolPoiIds = providerPool
            .Select(candidate => candidate.Candidate.PoiId)
            .ToFrozenSet();

        if (!_providerEnabled)
        {
            LogOutcome(
                LogLevel.Information,
                candidates.Length,
                providerPool.Length,
                latencyMilliseconds: 0,
                outcome: "provider-skipped",
                fallbackUsed: false,
                timedOut: false);
            return CreateBaseSnapshot(
                providerPool,
                providerPoolPoiIds,
                PoiRankingOutcomeCategory.ProviderSkipped);
        }

        var request = new PoiRankingRequest(
            providerPool
                .Select(candidate => new PoiRankingCandidate(
                    candidate.Candidate.PoiId,
                    candidate.Candidate.Name,
                    candidate.Candidate.CategoryName,
                    candidate.Candidate.TagNames.ToArray()))
                .ToArray(),
            new PoiRankingContext(preferenceTokens.ToArray()));

        Result<PoiRankingResult> providerResult;
        Stopwatch providerLatency = Stopwatch.StartNew();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.ProviderTimeout);

            try
            {
                providerResult = await _provider.RankAsync(request, timeout.Token);
                providerLatency.Stop();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                providerLatency.Stop();
                LogOutcome(
                    LogLevel.Information,
                    candidates.Length,
                    providerPool.Length,
                    providerLatency.ElapsedMilliseconds,
                    outcome: "cancelled",
                    fallbackUsed: false,
                    timedOut: false);
                throw;
            }
            catch (OperationCanceledException)
            {
                providerLatency.Stop();
                LogOutcome(
                    LogLevel.Warning,
                    candidates.Length,
                    providerPool.Length,
                    providerLatency.ElapsedMilliseconds,
                    outcome: "timeout",
                    fallbackUsed: true,
                    timedOut: true);
                return CreateBaseSnapshot(
                    providerPool,
                    providerPoolPoiIds,
                    PoiRankingOutcomeCategory.Timeout);
            }
        }

        if (providerResult is null || providerResult.IsFailure)
        {
            PoiRankingOutcomeCategory outcomeCategory = MapFailure(providerResult?.ErrorCode);
            LogOutcome(
                LogLevel.Warning,
                candidates.Length,
                providerPool.Length,
                providerLatency.ElapsedMilliseconds,
                OutcomeName(outcomeCategory),
                fallbackUsed: true,
                timedOut: false);
            return CreateBaseSnapshot(
                providerPool,
                providerPoolPoiIds,
                outcomeCategory);
        }

        if (!TryValidateProviderResult(
                providerResult.Value,
                providerPoolPoiIds,
                out FrozenDictionary<long, ValidatedPoiRankingItem> validatedItems))
        {
            LogOutcome(
                LogLevel.Warning,
                candidates.Length,
                providerPool.Length,
                providerLatency.ElapsedMilliseconds,
                outcome: "invalid-response",
                fallbackUsed: true,
                timedOut: false);
            return CreateBaseSnapshot(
                providerPool,
                providerPoolPoiIds,
                PoiRankingOutcomeCategory.InvalidResponse);
        }

        LogOutcome(
            LogLevel.Information,
            candidates.Length,
            providerPool.Length,
            providerLatency.ElapsedMilliseconds,
            outcome: "success",
            fallbackUsed: false,
            timedOut: false);

        return new PoiRankingSnapshot(
            providerPoolPoiIds,
            providerPool.ToFrozenDictionary(
                candidate => candidate.Candidate.PoiId,
                candidate => CreateSnapshotEntry(
                    candidate,
                    (_options.BaseWeight * candidate.BaseScore)
                    + (_options.AiWeight
                        * validatedItems[candidate.Candidate.PoiId].AiScore))),
            PoiRankingOutcomeCategory.Success);
    }

    internal static bool TryValidateProviderResult(
        PoiRankingResult? result,
        FrozenSet<long> providerPoolPoiIds,
        out FrozenDictionary<long, ValidatedPoiRankingItem> validatedItems)
    {
        ArgumentNullException.ThrowIfNull(providerPoolPoiIds);
        var items = new Dictionary<long, ValidatedPoiRankingItem>();

        if (result?.Ranked is null || result.Ranked.Count != providerPoolPoiIds.Count)
        {
            validatedItems = items.ToFrozenDictionary();
            return false;
        }

        foreach (PoiRankingItem? item in result.Ranked)
        {
            if (item is null
                || !providerPoolPoiIds.Contains(item.PoiId)
                || item.AiScore < 0m
                || item.AiScore > 1m)
            {
                validatedItems = items.ToFrozenDictionary();
                return false;
            }

            string? reason = item.Reason?.Trim();
            if (reason?.Length > MaximumReasonLength)
            {
                reason = reason[..MaximumReasonLength];
            }

            if (!items.TryAdd(
                    item.PoiId,
                    new ValidatedPoiRankingItem(item.PoiId, item.AiScore, reason)))
            {
                validatedItems = items.ToFrozenDictionary();
                return false;
            }
        }

        validatedItems = items.ToFrozenDictionary();
        return items.Count == providerPoolPoiIds.Count;
    }

    private ScoredCandidate ScoreCandidate(
        PoiRankingInputCandidate candidate,
        IReadOnlyCollection<string> preferenceTokens,
        PersonalBehaviorAggregation aggregation)
    {
        PersonalizationPoiFeatures features = PersonalizationFeatureBuilder.Build(
            preferenceTokens,
            candidate.CategoryName,
            candidate.TagNames,
            candidate.ScenicScore,
            candidate.PhotoRating);
        decimal behaviorAffinity = _behaviorScorer.Score(
            candidate.PoiId,
            candidate.CategoryId,
            aggregation);

        return new ScoredCandidate(
            candidate,
            _baseScorer.Score(features, behaviorAffinity));
    }

    private static PoiRankingSnapshot EmptySnapshot() => new(
        Array.Empty<long>().ToFrozenSet(),
        new Dictionary<long, PoiRankingSnapshotEntry>().ToFrozenDictionary(),
        PoiRankingOutcomeCategory.ProviderSkipped);

    private static PoiRankingSnapshot CreateBaseSnapshot(
        IReadOnlyCollection<ScoredCandidate> providerPool,
        FrozenSet<long> providerPoolPoiIds,
        PoiRankingOutcomeCategory outcomeCategory) =>
        new(
            providerPoolPoiIds,
            providerPool.ToFrozenDictionary(
                candidate => candidate.Candidate.PoiId,
                candidate => CreateSnapshotEntry(candidate, candidate.BaseScore)),
            outcomeCategory);

    private static PoiRankingSnapshotEntry CreateSnapshotEntry(
        ScoredCandidate scoredCandidate,
        decimal effectiveDesirabilityScore) =>
        new(
            scoredCandidate.Candidate.PoiId,
            scoredCandidate.BaseScore,
            effectiveDesirabilityScore,
            scoredCandidate.Candidate.ScenicScore,
            scoredCandidate.Candidate.PhotoRating,
            scoredCandidate.Candidate.ExplorationDistanceForRanking,
            scoredCandidate.Candidate.EstimatedVisitCostForRanking);

    private static PoiRankingOutcomeCategory MapFailure(string? errorCode) => errorCode switch
    {
        PoiRankingProviderErrorCodes.Quota => PoiRankingOutcomeCategory.Quota,
        PoiRankingProviderErrorCodes.Network => PoiRankingOutcomeCategory.Network,
        PoiRankingProviderErrorCodes.ServerError => PoiRankingOutcomeCategory.ServerError,
        PoiRankingProviderErrorCodes.InvalidResponse =>
            PoiRankingOutcomeCategory.InvalidResponse,
        _ => PoiRankingOutcomeCategory.InvalidResponse,
    };

    private static string OutcomeName(PoiRankingOutcomeCategory outcomeCategory) =>
        outcomeCategory switch
        {
            PoiRankingOutcomeCategory.Success => "success",
            PoiRankingOutcomeCategory.ProviderSkipped => "provider-skipped",
            PoiRankingOutcomeCategory.Timeout => "timeout",
            PoiRankingOutcomeCategory.Quota => "quota",
            PoiRankingOutcomeCategory.Network => "network",
            PoiRankingOutcomeCategory.ServerError => "server-error",
            PoiRankingOutcomeCategory.InvalidResponse => "invalid-response",
            _ => throw new ArgumentOutOfRangeException(nameof(outcomeCategory)),
        };

    private void LogOutcome(
        LogLevel level,
        int candidateCount,
        int poolCount,
        long latencyMilliseconds,
        string outcome,
        bool fallbackUsed,
        bool timedOut) =>
        _logger.Log(
            level,
            "POI ranking provider {Provider}; enabled={Enabled}; candidates={CandidateCount}; pool={PoolCount}; latencyMs={LatencyMs}; outcome={Outcome}; fallbackUsed={FallbackUsed}; timedOut={TimedOut}.",
            _provider.GetType().Name,
            _providerEnabled,
            candidateCount,
            poolCount,
            latencyMilliseconds,
            outcome,
            fallbackUsed,
            timedOut);

    private sealed record ScoredCandidate(
        PoiRankingInputCandidate Candidate,
        decimal BaseScore);
}