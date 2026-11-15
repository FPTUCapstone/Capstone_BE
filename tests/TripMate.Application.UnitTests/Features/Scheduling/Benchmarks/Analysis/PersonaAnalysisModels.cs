using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public sealed record PersonaJaccardPair(
    string PersonaIdA,
    string PersonaIdB,
    IReadOnlyList<long> VisitedPoiIdsA,
    IReadOnlyList<long> VisitedPoiIdsB,
    int StopCountA,
    int StopCountB,
    int IntersectionCount,
    int UnionCount,
    double Jaccard);

public sealed record PersonaJaccardMatrix(
    IReadOnlyList<string> PersonaIds,
    double[,] Matrix,
    IReadOnlyList<PersonaJaccardPair> Pairs);

public sealed record PersonaPairGateBreakdown(
    IReadOnlyList<PersonaJaccardPair> PersonalizedPairsBelowThreshold,
    IReadOnlyList<PersonaJaccardPair> PersonalizedPairsAboveOrEqualThreshold,
    IReadOnlyList<PersonaJaccardPair> ColdStartPairs);

public sealed record PersonaBaselineResult(
    string PersonaId,
    string DisplayName,
    bool Feasible,
    IReadOnlyList<long> VisitedPoiIds,
    int StopCount,
    int TotalMatrixTravelMinutes,
    int DiversityGroupCount,
    int MiddayOutdoorVisitCount,
    long? ModelObjective);

public sealed record AblationVariantResult(
    string VariantName,
    string Description,
    string PersonaId,
    bool Feasible,
    IReadOnlyList<long> VisitedPoiIds,
    double JaccardWithFull,
    IReadOnlyList<long> AddedPoiIds,
    IReadOnlyList<long> RemovedPoiIds,
    int StopCount,
    int TotalMatrixTravelMinutes,
    int DiversityGroupCount,
    int MiddayOutdoorVisitCount,
    long? ModelObjective,
    string Notes);

public sealed record SensitivityPointResult(
    string ParameterName,
    string Level,
    double Value,
    string PersonaId,
    bool Feasible,
    IReadOnlyList<long> VisitedPoiIds,
    double JaccardWithBase,
    int StopCount,
    int TotalMatrixTravelMinutes,
    int DiversityGroupCount,
    int MiddayOutdoorVisitCount,
    long? ModelObjective,
    string Notes);

public sealed record PersonaAnalysisRunResult(
    CityBenchmarkScenarioData CityData,
    IReadOnlyList<PersonaBaselineResult> Baselines,
    PersonaJaccardMatrix JaccardMatrix,
    IReadOnlyList<AblationVariantResult> Ablations,
    IReadOnlyList<SensitivityPointResult> Sensitivities,
    IReadOnlyList<PersonaJaccardPair> PairsBelowThreshold,
    IReadOnlyList<PersonaJaccardPair> PairsAboveOrEqualThreshold,
    int SuccessCount = 0,
    int ProviderSkippedCount = 0,
    int FallbackCount = 0)
{
    public bool ColdStartGatePassed
    {
        get
        {
            var coldStartPersonaIds = GetColdStartPersonaIds();
            return coldStartPersonaIds.Count == 0
                || coldStartPersonaIds.All(id =>
                    Baselines.FirstOrDefault(baseline => baseline.PersonaId == id) is
                    {
                        Feasible: true,
                        StopCount: > 0,
                        DiversityGroupCount: >= 2
                    });
        }
    }

    public PersonaPairGateBreakdown ClassifyPairs(double threshold)
    {
        var coldStartPersonaIds = GetColdStartPersonaIds();
        var personalizedPairs = JaccardMatrix.Pairs
            .Where(pair => !coldStartPersonaIds.Contains(pair.PersonaIdA)
                && !coldStartPersonaIds.Contains(pair.PersonaIdB))
            .ToArray();
        var coldStartPairs = JaccardMatrix.Pairs
            .Where(pair => coldStartPersonaIds.Contains(pair.PersonaIdA)
                || coldStartPersonaIds.Contains(pair.PersonaIdB))
            .ToArray();

        return new PersonaPairGateBreakdown(
            personalizedPairs.Where(pair => pair.Jaccard < threshold).ToArray(),
            personalizedPairs.Where(pair => pair.Jaccard >= threshold).ToArray(),
            coldStartPairs);
    }

    private IReadOnlySet<string> GetColdStartPersonaIds() => CityData.Personas
        .Where(persona => persona.Tags.Count == 0)
        .Select(persona => persona.Id)
        .ToHashSet(StringComparer.Ordinal);
}