namespace TripMate.Application.UnitTests.Features.Scheduling.Golden;

public sealed record SolverGoldenSnapshot(
    string ScenarioId,
    string Source,
    bool ExpectedFeasible,
    bool Feasible,
    IReadOnlyList<long>? BestVisitPoiIds,
    IReadOnlyList<IReadOnlyList<long>> EliteVisitIds,
    int? BestTravelMinutes,
    int? BestFinish,
    // CspStatistics (loại trừ Elapsed vì biến thiên theo phần cứng)
    int NodesExpanded,
    int PrunedByConstraints,
    int PrunedByForwardChecking,
    int PrunedByBound,
    int SolutionsFound,
    int? NodesToFirstSolution,
    bool SearchCompleted,
    bool NodeLimitReached,
    bool TimeLimitReached,
    bool InitialIncumbentAccepted,
    bool ReturnedInitialIncumbent,
    int EvaluatorCalls,
    bool InSearchEvaluationLimitReached,
    bool TieEvaluationLimitReached,
    bool UsedRelaxedRestModel,
    bool ChoseRelaxedRestModel,
    // Đo lường drift mô hình vs evaluator phục vụ PR-06
    bool DriftMeasured,
    int MaxModelVsEvaluatorDriftMinutes,
    int TravelMinutesModelVsEvaluatorDiff);
