using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

public sealed class ItineraryGenerationService(
    IRouteDurationProvider routeDurationProvider,
    SchedulingGenerationOptions? options = null,
    ItineraryScheduleEvaluator? evaluator = null,
    OptionalRouteOptimizer? optimizer = null,
    GeneratedItineraryInvariantValidator? validator = null)
{
    private const string ConstraintsInfeasible = "planning.constraints_infeasible";
    private readonly SchedulingGenerationOptions _options = options ?? new SchedulingGenerationOptions();
    private readonly ItineraryScheduleEvaluator _evaluator = evaluator ?? new ItineraryScheduleEvaluator(options);
    private readonly OptionalRouteOptimizer _optimizer = optimizer ?? new OptionalRouteOptimizer(evaluator ?? new ItineraryScheduleEvaluator(options), options);
    private readonly GeneratedItineraryInvariantValidator _validator = validator ?? new GeneratedItineraryInvariantValidator(options);

    public async Task<Result<GeneratedItineraryPlan>> GenerateAsync(
        GenerationInput input,
        CancellationToken cancellationToken)
    {
        if (HasInvalidRequestConstraints(input))
        {
            return Infeasible("The itinerary request is invalid.");
        }

        if (!TryBuildCandidateMap(input.Candidates, out var candidatesById)
            || input.MandatoryPoiIds.Any(id => !candidatesById.ContainsKey(id)))
        {
            return Infeasible("A mandatory location is unavailable.");
        }

        var mandatoryCandidates = input.MandatoryPoiIds
            .Select(id => candidatesById[id])
            .ToArray();
        if (input.BudgetVnd.HasValue && mandatoryCandidates.Any(candidate =>
                candidate.EstimatedVisitCost is null))
        {
            return Infeasible("A mandatory location has an unknown estimated cost.");
        }

        var routePoints = new List<RoutePoint> { input.Start };
        routePoints.AddRange(input.Candidates.Select(candidate => candidate.Location));
        routePoints.Add(input.End);
        var matrix = await routeDurationProvider.GetMatrixAsync(
            routePoints,
            input.TransportMode,
            cancellationToken);
        if (matrix.PointCount != routePoints.Count)
        {
            throw new InvalidOperationException("Route provider returned a matrix with an unexpected size.");
        }

        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = BuildCandidateMatrixIndices(matrixCandidates);

        var optimizationResult = TrySolveWithMiniRouting(
                input,
                matrix,
                matrixCandidates,
                candidateMatrixIndices,
                cancellationToken)
            ?? _optimizer.Optimize(
                input,
                mandatoryCandidates,
                matrix,
                matrixCandidates,
                candidateMatrixIndices,
                cancellationToken);

        if (optimizationResult is null)
        {
            return Infeasible("The mandatory locations cannot fit within the selected time, hours, budget, and end point.");
        }

        _validator.Validate(input, matrix, matrixCandidates, optimizationResult.Schedule.Plan);

        var baselineOptionalCount = optimizationResult.BaselineSchedule.VisitPoiIds.Count(id => !input.MandatoryPoiIds.Contains(id));
        var finalOptionalCount = optimizationResult.Schedule.VisitPoiIds.Count(id => !input.MandatoryPoiIds.Contains(id));

        RouteOptimizationDiagnostics.RecordOptimization(
            enabled: _options.EnableOptionalRouteOptimization,
            baselineOptionalCount: baselineOptionalCount,
            finalOptionalCount: finalOptionalCount,
            baselineTravelMinutes: optimizationResult.BaselineSchedule.TotalMatrixTravelMinutes,
            finalTravelMinutes: optimizationResult.Schedule.TotalMatrixTravelMinutes,
            evaluationsCount: optimizationResult.EvaluationsCount,
            seedCount: optimizationResult.SeedCount,
            reconsideredAdmissionsCount: optimizationResult.ReconsideredAdmissionsCount,
            twoOptMovesCount: optimizationResult.TwoOptMovesCount,
            relocateMovesCount: optimizationResult.RelocateMovesCount,
            budgetExhausted: optimizationResult.BudgetExhausted,
            elapsedOptimization: optimizationResult.ElapsedOptimization);

        return Result.Success(optimizationResult.Schedule.Plan);
    }

    public async Task<Result<GeneratedItineraryPlan>> GenerateFixedOrderAsync(
        GenerationInput input,
        IReadOnlyList<long> orderedVisitPoiIds,
        CancellationToken cancellationToken)
    {
        if (HasInvalidRequestConstraints(input))
        {
            return Infeasible("The itinerary request is invalid.");
        }

        if (orderedVisitPoiIds.Count == 0
            || orderedVisitPoiIds.Distinct().Count() != orderedVisitPoiIds.Count)
        {
            return Infeasible("At least one distinct visit location is required.");
        }

        if (!TryBuildCandidateMap(input.Candidates, out var candidates)
            || orderedVisitPoiIds.Any(id => !candidates.ContainsKey(id)))
        {
            return Infeasible("A selected visit location is unavailable.");
        }

        if (input.MandatoryPoiIds.Any(id => !orderedVisitPoiIds.Contains(id)))
        {
            return Infeasible("Mandatory locations cannot be removed from an itinerary.");
        }

        var matrixCandidates = input.Candidates.ToArray();
        var routePoints = new List<RoutePoint> { input.Start };
        routePoints.AddRange(matrixCandidates.Select(candidate => candidate.Location));
        routePoints.Add(input.End);
        var matrix = await routeDurationProvider.GetMatrixAsync(
            routePoints,
            input.TransportMode,
            cancellationToken);
        if (matrix.PointCount != routePoints.Count)
        {
            throw new InvalidOperationException("Route provider returned a matrix with an unexpected size.");
        }

        var candidateMatrixIndices = BuildCandidateMatrixIndices(matrixCandidates);
        var orderedCandidates = orderedVisitPoiIds.Select(id => candidates[id]).ToArray();
        var schedule = _evaluator.Evaluate(
            input,
            orderedCandidates,
            matrix,
            matrixCandidates,
            candidateMatrixIndices,
            cancellationToken);

        if (schedule is null)
        {
            return Infeasible("The selected visit order cannot fit within the selected constraints.");
        }

        _validator.Validate(input, matrix, matrixCandidates, schedule.Plan);
        return Result.Success(schedule.Plan);
    }

    /// <summary>
    /// Chạy <see cref="MiniRoutingSolver"/> khi <see cref="SchedulingGenerationOptions.SolverMode"/>
    /// là <see cref="SchedulingSolverMode.MiniRouting"/>. Trả về null để bên gọi dùng
    /// <see cref="OptionalRouteOptimizer"/> khi chế độ tắt hoặc bộ giải không tìm được lời giải.
    /// </summary>
    private OptimizationResult? TrySolveWithMiniRouting(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        CancellationToken cancellationToken)
    {
        if (_options.SolverMode != SchedulingSolverMode.MiniRouting)
        {
            return null;
        }

        var result = new MiniRoutingSolver(_evaluator, _options, _options.MiniRouting).Solve(
            input,
            matrix,
            matrixCandidates,
            candidateMatrixIndices,
            cancellationToken);

        if (result is null)
        {
            return null;
        }

        var moves = result.Statistics.AcceptedMoves;
        return new OptimizationResult(
            result.Schedule,
            result.InitialSchedule,
            EvaluationsCount: result.Statistics.MovesEvaluated,
            SeedCount: 1,
            ReconsideredAdmissionsCount: moves.GetValueOrDefault(RoutingMoveKind.InsertOptional),
            TwoOptMovesCount: moves.GetValueOrDefault(RoutingMoveKind.TwoOpt),
            RelocateMovesCount: moves.GetValueOrDefault(RoutingMoveKind.Relocate)
                + moves.GetValueOrDefault(RoutingMoveKind.OrOpt)
                + moves.GetValueOrDefault(RoutingMoveKind.Exchange),
            BudgetExhausted: result.Statistics.TimeLimitReached,
            ElapsedOptimization: result.Statistics.Elapsed);
    }

    private static bool HasInvalidRequestConstraints(GenerationInput input) =>
        input.AvailableMinutes is < 60 or > 720
        || input.BudgetVnd is <= 0
        || input.MandatoryPoiIds.Count > 6
        || input.MandatoryPoiIds.Distinct().Count() != input.MandatoryPoiIds.Count;

    private static Dictionary<long, int> BuildCandidateMatrixIndices(IReadOnlyList<GenerationCandidate> candidates)
    {
        var map = new Dictionary<long, int>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            map[candidates[i].Id] = i + 1;
        }

        return map;
    }

    private static bool TryBuildCandidateMap(
        IReadOnlyCollection<GenerationCandidate> candidates,
        out Dictionary<long, GenerationCandidate> candidateMap)
    {
        candidateMap = new Dictionary<long, GenerationCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!candidateMap.TryAdd(candidate.Id, candidate))
            {
                candidateMap = null!;
                return false;
            }
        }

        return true;
    }

    private static Result<GeneratedItineraryPlan> Infeasible(string message) =>
        Result.Failure<GeneratedItineraryPlan>(ConstraintsInfeasible, message);
}