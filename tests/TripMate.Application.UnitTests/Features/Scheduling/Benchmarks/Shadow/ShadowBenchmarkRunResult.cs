using TripMate.Application.Features.Scheduling.Csp;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public sealed record ShadowBenchmarkRunResult(
    string ScenarioId,
    string PersonaId,
    ObjectiveMode Mode,
    bool Feasible,
    int StopCount,
    int TotalMatrixTravelMinutes,
    int DiversityGroupCount,
    int MiddayOutdoorVisitCount,
    int MiddayOutdoorVisitDenominator,
    double MiddayOutdoorVisitRate,
    long? ModelObjective,
    IReadOnlyList<long> VisitedPoiIds,
    IReadOnlyList<double> DurationSamplesMs,
    double P50Milliseconds,
    double P95Milliseconds);

public sealed record ShadowBenchmarkReportMetadata(
    string RunDate,
    string BuildConfiguration,
    string OperatingSystem,
    string Runtime,
    string Machine,
    int LogicalProcessorCount,
    int WarmupIterations);

public sealed record ShadowBenchmarkSeedComparison(
    string PersonaId,
    ObjectiveMode Mode,
    int BaselineStopCount,
    int V3bStopCount,
    int BaselineTravelMinutes,
    int V3bTravelMinutes,
    int BaselineDiversityGroupCount,
    int V3bDiversityGroupCount,
    int BaselineMiddayOutdoorCount,
    int V3bMiddayOutdoorCount,
    long? BaselineModelObjective,
    long? V3bModelObjective,
    IReadOnlyList<long> BaselineVisitedPoiIds,
    IReadOnlyList<long> V3bVisitedPoiIds);