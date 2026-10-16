using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Constants;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// Elastic visit duration policy: when fitting an extra or modified stop, POI stay durations may be
/// compressed by up to 25% of their default catalog duration, subject to a strict minimum floor
/// (30 minutes for sightseeing/attractions, 35 minutes for food and drink).
/// </summary>
public static class ElasticVisitCompression
{
    public const decimal MaxCompressionRatio = 0.25m;
    public const int CulturalMinDurationFloorMinutes = 30;
    public const int DiningMinDurationFloorMinutes = 35;

    public static int GetMinFloorMinutes(string? categoryName) =>
        PoiCategoryNames.IsFoodAndDrink(categoryName)
            ? DiningMinDurationFloorMinutes
            : CulturalMinDurationFloorMinutes;

    public static int CalculateMinDuration(int defaultDurationMinutes, string? categoryName)
    {
        var floor = GetMinFloorMinutes(categoryName);
        var compressed = (int)Math.Ceiling(defaultDurationMinutes * (1m - MaxCompressionRatio));
        return Math.Max(floor, compressed);
    }

    public static int MaxReductionMinutes(GenerationCandidate candidate)
    {
        var minAllowed = CalculateMinDuration(candidate.VisitDurationMinutes, candidate.CategoryName);
        return Math.Max(0, candidate.VisitDurationMinutes - minAllowed);
    }

    /// <summary>
    /// Applies elastic duration compression to candidates appearing in <paramref name="orderedVisitPoiIds"/>.
    /// If <paramref name="neededMinutes"/> is specified, distributes the reduction proportionally among stops.
    /// If null, compresses all eligible stops to their maximum allowable reduction (25% down to floor).
    /// </summary>
    public static GenerationInput ApplyCompression(
        GenerationInput input,
        IReadOnlyList<long> orderedVisitPoiIds,
        int? neededMinutes = null)
    {
        var candidateMap = input.Candidates.ToDictionary(c => c.Id);
        var reductions = new Dictionary<long, int>();

        var compressibleStops = orderedVisitPoiIds
            .Where(candidateMap.ContainsKey)
            .Select(id => (Id: id, Candidate: candidateMap[id], MaxRed: MaxReductionMinutes(candidateMap[id])))
            .Where(x => x.MaxRed > 0)
            .ToList();

        var totalCompressible = compressibleStops.Sum(x => x.MaxRed);
        if (totalCompressible == 0)
        {
            return input;
        }

        if (neededMinutes is null || neededMinutes.Value >= totalCompressible)
        {
            foreach (var stop in compressibleStops)
            {
                reductions[stop.Id] = stop.MaxRed;
            }
        }
        else if (neededMinutes.Value > 0)
        {
            var target = neededMinutes.Value;
            var allocated = 0;
            foreach (var stop in compressibleStops)
            {
                var share = (int)Math.Round((double)target * stop.MaxRed / totalCompressible);
                share = Math.Clamp(share, 0, stop.MaxRed);
                reductions[stop.Id] = share;
                allocated += share;
            }

            var diff = target - allocated;
            if (diff != 0)
            {
                foreach (var stop in compressibleStops)
                {
                    if (diff > 0 && reductions[stop.Id] < stop.MaxRed)
                    {
                        var add = Math.Min(diff, stop.MaxRed - reductions[stop.Id]);
                        reductions[stop.Id] += add;
                        diff -= add;
                    }
                    else if (diff < 0 && reductions[stop.Id] > 0)
                    {
                        var sub = Math.Min(-diff, reductions[stop.Id]);
                        reductions[stop.Id] -= sub;
                        diff += sub;
                    }

                    if (diff == 0)
                    {
                        break;
                    }
                }
            }
        }

        var newCandidates = input.Candidates.Select(c =>
        {
            if (reductions.TryGetValue(c.Id, out var red) && red > 0)
            {
                return c with { VisitDurationMinutes = c.VisitDurationMinutes - red };
            }

            return c;
        }).ToArray();

        return input with { Candidates = newCandidates };
    }
}