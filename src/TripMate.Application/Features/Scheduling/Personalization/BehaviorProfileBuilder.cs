using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Personalization;

/// <summary>One behavior event, reduced to what the profile needs.</summary>
public sealed record BehaviorSignal(
    long PoiId,
    RecommendationEventType EventType,
    RecommendationCaptureSource Source,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// UC-10 item 6 (spec .scratch/uc10-behavior-learning §3.3, decisions D2/D3): turns a traveler's behavior events
/// into the profile the AI ranking learns from. Each signal counts by its kind
/// (<see cref="BehaviorSignalWeights"/>) and halves every <c>BehaviorHalfLifeDays</c>; opposite signals on one
/// POI cancel; the heaviest POIs per side are sent with their name, category and tags, because they are often
/// outside the current candidate pool.
/// </summary>
public sealed class BehaviorProfileBuilder(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    PersonalizationRankingOptions options)
{
    // Evidence below this is noise (for example a like and a removal that almost cancel).
    private const decimal MinimumNetWeight = 0.01m;

    /// <summary>
    /// Net evidence per POI: positive = liked, negative = disliked, heaviest first (the POI id breaks ties, so
    /// the same signals always give the same profile). Pure, so the learning benchmark reuses the exact math.
    /// </summary>
    public static IReadOnlyList<(long PoiId, decimal Net)> NetWeights(
        IEnumerable<BehaviorSignal> signals,
        DateTimeOffset now,
        PersonalizationRankingOptions options)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(options);

        var net = new Dictionary<long, double>();
        foreach (BehaviorSignal signal in signals)
        {
            double ageDays = Math.Max(0d, (now - signal.OccurredAtUtc).TotalDays);
            double decay = Math.Pow(0.5d, ageDays / options.BehaviorHalfLifeDays);
            double weight = (double)options.BehaviorSignalWeights.SignedWeight(signal.EventType, signal.Source) * decay;
            net[signal.PoiId] = net.GetValueOrDefault(signal.PoiId) + weight;
        }

        return net
            .Select(pair => (PoiId: pair.Key, Net: Math.Round((decimal)pair.Value, 4)))
            .Where(poi => Math.Abs(poi.Net) >= MinimumNetWeight)
            .OrderByDescending(poi => Math.Abs(poi.Net))
            .ThenBy(poi => poi.PoiId)
            .ToArray();
    }

    /// <summary>
    /// Decision D8: the POIs whose latest signal inside the cooldown window is a removal (Dislike) or a skip. A
    /// later Like (added back, marked mandatory, reached) lifts the cooldown. Pure, shared with the benchmark.
    /// </summary>
    public static IReadOnlySet<long> RemovedRecently(
        IEnumerable<BehaviorSignal> signals,
        DateTimeOffset now,
        int cooldownDays)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (cooldownDays <= 0)
        {
            return new HashSet<long>();
        }

        DateTimeOffset since = now.AddDays(-cooldownDays);
        // The signals keep their order when two share a timestamp, so the caller's "newest last" is respected.
        return signals
            .Where(signal => signal.OccurredAtUtc >= since && signal.EventType != RecommendationEventType.Reorder)
            .Select((signal, index) => (signal, index))
            .GroupBy(entry => entry.signal.PoiId)
            .Select(group => group.OrderBy(entry => entry.signal.OccurredAtUtc).ThenBy(entry => entry.index).Last().signal)
            .Where(latest => latest.EventType is RecommendationEventType.Dislike or RecommendationEventType.Skip)
            .Select(latest => latest.PoiId)
            .ToHashSet();
    }

    /// <summary>The stops this traveler removed or skipped recently and has not chosen again (decision D8).</summary>
    public async Task<IReadOnlySet<long>> RemovedRecentlyAsync(long travelerUserId, CancellationToken cancellationToken)
    {
        if (options.RemovedStopCooldownDays <= 0)
        {
            return new HashSet<long>();
        }

        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset since = now.AddDays(-options.RemovedStopCooldownDays);
        var signals = await dbContext.RecommendationBehaviorEvents
            .AsNoTracking()
            .Where(signal =>
                signal.TravelerUserId == travelerUserId
                && signal.OccurredAtUtc >= since
                && (signal.EventType == RecommendationEventType.Like
                    || signal.EventType == RecommendationEventType.Dislike
                    || signal.EventType == RecommendationEventType.Skip))
            .OrderBy(signal => signal.OccurredAtUtc)
            .ThenBy(signal => signal.Id)
            .Select(signal => new BehaviorSignal(
                signal.PointOfInterestId,
                signal.EventType,
                signal.Source,
                signal.OccurredAtUtc))
            .ToListAsync(cancellationToken);
        return RemovedRecently(signals, now, options.RemovedStopCooldownDays);
    }

    /// <summary>The traveler's profile, or null when there is no usable evidence.</summary>
    public async Task<PoiRankingBehavior?> BuildAsync(long travelerUserId, CancellationToken cancellationToken)
    {
        if (options.MaxBehaviorPoisPerSide == 0)
        {
            return null;
        }

        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset since = now.AddDays(-options.BehaviorLookbackDays);
        var signals = await dbContext.RecommendationBehaviorEvents
            .AsNoTracking()
            .Where(signal =>
                signal.TravelerUserId == travelerUserId
                && signal.OccurredAtUtc >= since
                && (signal.EventType == RecommendationEventType.Like
                    || signal.EventType == RecommendationEventType.Dislike
                    || signal.EventType == RecommendationEventType.Skip))
            .OrderByDescending(signal => signal.OccurredAtUtc)
            .ThenByDescending(signal => signal.Id)
            .Take(options.MaxBehaviorEvents)
            .Select(signal => new BehaviorSignal(
                signal.PointOfInterestId,
                signal.EventType,
                signal.Source,
                signal.OccurredAtUtc))
            .ToListAsync(cancellationToken);
        if (signals.Count == 0)
        {
            return null;
        }

        var weighted = NetWeights(signals, now, options);
        var liked = weighted.Where(poi => poi.Net > 0m).Take(options.MaxBehaviorPoisPerSide).ToArray();
        var disliked = weighted.Where(poi => poi.Net < 0m).Take(options.MaxBehaviorPoisPerSide).ToArray();
        if (liked.Length == 0 && disliked.Length == 0)
        {
            return null;
        }

        long[] poiIds = liked.Concat(disliked).Select(poi => poi.PoiId).ToArray();
        var details = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Where(poi => poiIds.Contains(poi.Id))
            .Select(poi => new
            {
                poi.Id,
                poi.Name,
                CategoryName = poi.Category.Name,
                TagNames = poi.PoiTags.Select(mapping => mapping.Tag.Name).ToArray(),
            })
            .ToDictionaryAsync(poi => poi.Id, cancellationToken);

        PoiRankingBehaviorPoi[] Describe(IEnumerable<(long PoiId, decimal Net)> side) =>
            side.Where(poi => details.ContainsKey(poi.PoiId))
                .Select(poi => new PoiRankingBehaviorPoi(
                    poi.PoiId,
                    details[poi.PoiId].Name,
                    details[poi.PoiId].CategoryName,
                    details[poi.PoiId].TagNames,
                    Math.Abs(poi.Net)))
                .ToArray();

        var profile = new PoiRankingBehavior(Describe(liked), Describe(disliked));
        return profile.Liked.Count == 0 && profile.Disliked.Count == 0 ? null : profile;
    }
}