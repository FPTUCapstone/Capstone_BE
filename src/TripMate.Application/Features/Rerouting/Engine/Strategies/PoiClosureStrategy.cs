using TripMate.Application.Features.Rerouting.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Engine.Strategies;

/// <summary>
/// Spec 4.3 <c>POIClosure</c> on stop A: (1) swap A for a same-category POI (not for a mandatory stop),
/// (2) move A to a later slot where it is open (allowed for a mandatory stop; the evaluator rejects a slot where it is
/// still closed), (3) drop A (not a mandatory stop).
/// </summary>
internal sealed class PoiClosureStrategy : IReroutingStrategy
{
    public string IncidentType => Incident.PoiClosureType;

    public async Task<IReadOnlyList<IReadOnlyList<ReroutingVariant>>> CreateBatchesAsync(
        ReroutingStrategyContext context,
        CancellationToken cancellationToken)
    {
        var poiId = context.AffectedPoi.Id;
        var batches = new List<IReadOnlyList<ReroutingVariant>>();

        if (!context.Affected.IsMandatory)
        {
            var replacements = await context.Candidates.FindAsync(
                new ReroutingCandidateQuery(
                    context.AffectedPoi,
                    context.Anchor.CandidateRadiusKm,
                    context.ExcludedPoiIds,
                    context.PreferenceTokens,
                    context.MaxReplacementsPerStop,
                    RequireSameCategory: true),
                cancellationToken);
            if (replacements.Count > 0)
            {
                batches.Add(replacements
                    .Select(replacement => new ReroutingVariant(
                        ReroutingStrategies.ReplaceSameCategory,
                        ReroutingVariants.Replace(context, poiId, replacement.Id),
                        [poiId]))
                    .ToArray());
            }
        }

        var order = ReroutingVariants.PendingPoiIds(context).ToList();
        var index = order.IndexOf(poiId);
        var postpones = new List<ReroutingVariant>();
        for (var target = index + 1; target < order.Count; target++)
        {
            var moved = order.Where(id => id != poiId).ToList();
            moved.Insert(target, poiId);
            postpones.Add(new ReroutingVariant(ReroutingStrategies.Postpone, ReroutingVariants.Stops(moved), []));
        }

        if (postpones.Count > 0)
        {
            batches.Add(postpones.Take(context.MaxVariants).ToArray());
        }

        if (!context.Affected.IsMandatory && context.Pending.Count > 1)
        {
            batches.Add([new ReroutingVariant(
                ReroutingStrategies.Drop,
                ReroutingVariants.Without(context, poiId),
                [poiId])]);
        }

        return batches;
    }
}