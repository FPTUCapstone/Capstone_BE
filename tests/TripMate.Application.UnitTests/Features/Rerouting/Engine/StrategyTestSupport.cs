using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Rerouting.Engine;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Rerouting.Engine;

internal static class StrategyTestSupport
{
    public static readonly DateTimeOffset EvaluatedAt = ReroutingTestKit.RequestStart.AddMinutes(30);

    public static async Task<ReroutingStrategyContext> ContextAsync(
        ReroutingTestKit kit,
        TripSession session,
        PointOfInterest affected,
        decimal radiusKm = 5m,
        IReadOnlySet<string>? preferenceTokens = null,
        IReadOnlyDictionary<long, int>? preferenceScores = null,
        int maxVariants = 8,
        int maxReplacementsPerStop = 3)
    {
        var affectedItem = session.Items.Single(item => item.PoiId == affected.Id);
        var pending = session.Items
            .Where(item => item.Status == TripSessionItem.PendingStatus)
            .OrderBy(item => item.SequenceNo)
            .ToArray();
        var costs = session.Items.ToDictionary(item => item.ItineraryItemId, _ => (decimal?)60_000m);
        var anchor = ReroutingAnchorFactory.Create(session, kit.Request, affectedItem, costs, radiusKm, EvaluatedAt);
        var affectedPoi = await kit.Db.PointsOfInterest
            .Include(poi => poi.Category)
            .Include(poi => poi.OpeningHours)
            .SingleAsync(poi => poi.Id == affected.Id);
        return new ReroutingStrategyContext(
            pending,
            affectedItem,
            affectedPoi,
            anchor,
            maxVariants,
            maxReplacementsPerStop,
            preferenceTokens ?? new HashSet<string>(),
            preferenceScores ?? new Dictionary<long, int>(),
            session.Items.Select(item => item.PoiId).ToHashSet(),
            new ReroutingCandidateFinder(kit.Db));
    }

    public static long[] Ids(ReroutingVariant variant) => variant.Stops.Select(stop => stop.PoiId).ToArray();
}