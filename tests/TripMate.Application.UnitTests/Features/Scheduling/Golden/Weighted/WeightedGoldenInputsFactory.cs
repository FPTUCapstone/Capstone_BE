using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

public static class WeightedGoldenInputsFactory
{
    public static double CalculateBaseScore(long poiId) =>
        0.2 + 0.8 * ((poiId * 2654435761L & 0x7FFFFFFF) % 1000) / 1000.0;

    public static WeightedRequestInputs CreateRequestInputs(IEnumerable<GenerationCandidate> candidates)
    {
        var baseByPoiId = new Dictionary<long, double>();
        foreach (var c in candidates)
        {
            baseByPoiId[c.Id] = CalculateBaseScore(c.Id);
        }

        var personalParams = new PersonalParams(
            LambdaPerMin: 0.015,
            Mu: 0.005,
            MaxStopsPerDay: 5,
            MaxDailyEffort: 300,
            RestAfterMin: 120,
            MaxConsecutiveOutdoor: 3,
            SunriseFactor: 0.6,
            GammaDefault: 0.6,
            GammaLoved: 0.85,
            LovedCategories: new HashSet<string>(),
            RhoOutdoor: 0.25,
            RhoRest: 0.20,
            RhoStops: 0.30,
            RhoEffortPerHour: 0.05,
            ConfigVersion: "golden-weighted-moderate");

        return new WeightedRequestInputs(
            baseByPoiId,
            personalParams,
            new ConstantTimeFitFactory());
    }
}