namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

public sealed record WeightedSolverGoldenSnapshot(
    string ScenarioId,
    string Source,
    bool Feasible,
    long? BestModelObjective,
    long? BestEvaluatedObjective,
    IReadOnlyList<long>? BestVisitPoiIds,
    IReadOnlyList<long> EliteModelObjectives,
    IReadOnlyList<IReadOnlyList<long>> EliteVisitIds,
    int? BestTravelMinutes,
    int? BestDurationMinutes,
    decimal? BestEstimatedCost,
    DateTimeOffset? EndAtUtc,
    // CspStatistics
    int NodesExpanded,
    int PrunedByConstraints,
    int PrunedByForwardChecking,
    int PrunedByBound,
    int SolutionsFound,
    int? NodesToFirstSolution,
    bool SearchCompleted,
    bool NodeLimitReached,
    bool TimeLimitReached,
    int EvaluatorCalls,
    bool UsedRelaxedRestModel,
    bool ChoseRelaxedRestModel);