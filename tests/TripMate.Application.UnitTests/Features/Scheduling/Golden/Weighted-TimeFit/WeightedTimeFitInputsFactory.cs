using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.WeightedTimeFit;

public static class WeightedTimeFitInputsFactory
{
    public static WeightedRequestInputs CreateRequestInputs(IEnumerable<GenerationCandidate> candidates)
    {
        var baseByPoiId = new Dictionary<long, double>();
        foreach (var c in candidates)
        {
            baseByPoiId[c.Id] = WeightedGoldenInputsFactory.CalculateBaseScore(c.Id);
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
            ConfigVersion: "golden-weighted-timefit-moderate");

        return new WeightedRequestInputs(
            baseByPoiId,
            personalParams,
            new RealTimeFitProviderFactory(personalParams));
    }
}