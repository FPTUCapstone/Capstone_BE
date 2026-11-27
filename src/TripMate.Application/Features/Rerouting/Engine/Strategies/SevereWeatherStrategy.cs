using TripMate.Application.Features.Rerouting.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Rerouting.Engine.Strategies;

/// <summary>
/// Spec 4.3 <c>SevereWeather</c> on a reviewed Outdoor, non-mandatory stop: (1) swap it for a reviewed Indoor POI,
/// (2) drop it. Anything else (an indoor stop, an unreviewed POI, a mandatory stop) has no proposal: the alert is
/// still delivered, the Traveler just gets no alternative.
/// </summary>
internal sealed class SevereWeatherStrategy : IReroutingStrategy
{
    public string IncidentType => Incident.SevereWeatherType;

    public async Task<IReadOnlyList<IReadOnlyList<ReroutingVariant>>> CreateBatchesAsync(
        ReroutingStrategyContext context,
        CancellationToken cancellationToken)
    {
        var poi = context.AffectedPoi;
        if (context.Affected.IsMandatory
            || poi.IndoorOutdoor != IndoorOutdoorType.Outdoor
            || !poi.IndoorOutdoorReviewedAtUtc.HasValue)
        {
            return [];
        }

        var batches = new List<IReadOnlyList<ReroutingVariant>>();
        var replacements = await context.Candidates.FindAsync(
            new ReroutingCandidateQuery(
                poi,
                context.Anchor.CandidateRadiusKm,
                context.ExcludedPoiIds,
                context.PreferenceTokens,
                context.MaxReplacementsPerStop,
                RequireReviewedIndoor: true),
            cancellationToken);
        if (replacements.Count > 0)
        {
            batches.Add(replacements
                .Select(replacement => new ReroutingVariant(
                    ReroutingStrategies.ReplaceWithIndoor,
                    ReroutingVariants.Replace(context, poi.Id, replacement.Id),
                    [poi.Id]))
                .ToArray());
        }

        if (context.Pending.Count > 1)
        {
            batches.Add([new ReroutingVariant(
                ReroutingStrategies.Drop,
                ReroutingVariants.Without(context, poi.Id),
                [poi.Id])]);
        }

        return batches;
    }
}