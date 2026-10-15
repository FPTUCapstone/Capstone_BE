using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Entities;
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

        OptimizationResult? alternativeResult = null;
        CspStatistics? cspStatistics = null;
        var outcome = SchedulingSolverOutcomes.Heuristic;

        // R4: tắt tối ưu tuyến thì bỏ qua SolverMode, chạy đúng đường không tối ưu của OptionalRouteOptimizer.
        var solverMode = _options.EnableOptionalRouteOptimization ? _options.SolverMode : SchedulingSolverMode.Heuristic;
        if (solverMode == SchedulingSolverMode.Csp)
        {
            // R1a: lịch heuristic là incumbent ban đầu của CSP, nên kết quả CSP không bao giờ kém nó theo product rule.
            // Nó cũng là lưới an toàn khi CSP không trả về lịch: CSP không mô hình hóa điểm nghỉ, nên bật CSP không bao
            // giờ làm mất một lịch trình mà hệ thống trước đây tìm được.
            var heuristic = _optimizer.Optimize(
                input,
                mandatoryCandidates,
                matrix,
                matrixCandidates,
                candidateMatrixIndices,
                cancellationToken);
            var csp = new CspItinerarySolver(_evaluator, _options).Solve(
                input,
                matrix,
                matrixCandidates,
                candidateMatrixIndices,
                heuristic?.Schedule,
                cancellationToken);
            cspStatistics = csp.Statistics;

            if (csp.Statistics.TimeLimitReached)
            {
                // R5: lưới an toàn thời gian bị chạm thì CSP đã bỏ kết quả dở dang; trả lịch heuristic nguyên vẹn để kết
                // quả không phụ thuộc vào lúc dừng.
                if (heuristic is null)
                {
                    SchedulingSolverDiagnostics.RecordOutcome(solverMode, SchedulingSolverOutcomes.Infeasible, cspStatistics);
                    return Infeasible("The mandatory locations cannot fit within the selected time, hours, budget, and end point.");
                }

                alternativeResult = heuristic;
                outcome = SchedulingSolverOutcomes.HeuristicFallbackCspTimeLimit;
            }
            else if (csp.Schedule is not null)
            {
                alternativeResult = ToOptimizationResult(csp, heuristic);
                outcome = SchedulingSolverOutcomes.Csp;
            }
            else if (heuristic is not null)
            {
                alternativeResult = heuristic;
                outcome = csp.ProvedInfeasible
                    ? SchedulingSolverOutcomes.HeuristicFallbackCspProvedInfeasible
                    : csp.Statistics.SolutionsFound > 0
                        ? SchedulingSolverOutcomes.HeuristicFallbackCspNoValidCandidate
                        : SchedulingSolverOutcomes.HeuristicFallbackCspSearchLimit;
            }
            else
            {
                SchedulingSolverDiagnostics.RecordOutcome(solverMode, SchedulingSolverOutcomes.Infeasible, cspStatistics);
                return Infeasible(csp.ProvedInfeasible
                    ? DescribeCspInfeasibility(csp, candidatesById)
                    : "The mandatory locations cannot fit within the selected time, hours, budget, and end point.");
            }
        }
        else if (solverMode == SchedulingSolverMode.MiniRouting)
        {
            alternativeResult = TrySolveWithMiniRouting(
                input,
                matrix,
                matrixCandidates,
                candidateMatrixIndices,
                cancellationToken);
            outcome = alternativeResult is not null
                ? SchedulingSolverOutcomes.MiniRouting
                : SchedulingSolverOutcomes.HeuristicFallbackMiniRouting;
        }

        var optimizationResult = alternativeResult
            ?? _optimizer.Optimize(
                input,
                mandatoryCandidates,
                matrix,
                matrixCandidates,
                candidateMatrixIndices,
                cancellationToken);

        if (optimizationResult is null)
        {
            SchedulingSolverDiagnostics.RecordOutcome(solverMode, SchedulingSolverOutcomes.Infeasible, cspStatistics);
            return Infeasible("The mandatory locations cannot fit within the selected time, hours, budget, and end point.");
        }

        _validator.Validate(input, matrix, matrixCandidates, optimizationResult.Schedule.Plan);
        SchedulingSolverDiagnostics.RecordOutcome(solverMode, outcome, cspStatistics);

        // CSP không có lịch gốc để so sánh và không có bước "cải thiện": số liệu của nó nằm trong
        // SchedulingSolverOutcome. Ghi activity OptionalRouteOptimization ở đây chỉ cho ra các bộ đếm luôn bằng 0.
        if (outcome == SchedulingSolverOutcomes.Csp)
        {
            return Result.Success(optimizationResult.Schedule.Plan);
        }

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
    /// Chạy <see cref="MiniRoutingSolver"/> (bên gọi chỉ gọi khi chế độ hiệu lực là
    /// <see cref="SchedulingSolverMode.MiniRouting"/>). Trả về null để bên gọi dùng
    /// <see cref="OptionalRouteOptimizer"/> khi bộ giải không tìm được lời giải.
    /// </summary>
    private OptimizationResult? TrySolveWithMiniRouting(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        CancellationToken cancellationToken)
    {
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

    private static OptimizationResult ToOptimizationResult(CspResult result, OptimizationResult? heuristic)
    {
        var stats = result.Statistics;
        return new OptimizationResult(
            result.Schedule!,
            heuristic?.Schedule ?? result.Schedule!,
            EvaluationsCount: stats.EvaluatorCalls,
            SeedCount: 1,
            ReconsideredAdmissionsCount: 0,
            TwoOptMovesCount: 0,
            RelocateMovesCount: 0,
            BudgetExhausted: !stats.SearchCompleted,
            ElapsedOptimization: stats.Elapsed);
    }

    /// <summary>
    /// CSP đã duyệt hết cây mà không có lời giải: nêu tên các điểm bắt buộc bị forward checking loại
    /// nhiều nhất, để người dùng biết nên bỏ hoặc đổi điểm nào.
    /// </summary>
    private static string DescribeCspInfeasibility(
        CspResult result,
        IReadOnlyDictionary<long, GenerationCandidate> candidatesById)
    {
        const string baseMessage = "The mandatory locations cannot fit within the selected time, hours, budget, and end point.";
        var names = result.ConflictingMandatoryPoiIds
            .Where(candidatesById.ContainsKey)
            .Select(id => candidatesById[id].Name)
            .ToArray();

        return names.Length == 0
            ? baseMessage
            : $"{baseMessage} Most conflicting: {string.Join(", ", names)}.";
    }

    private static bool HasInvalidRequestConstraints(GenerationInput input) =>
        input.AvailableMinutes is < SchedulingRequest.MinAvailableMinutes or > SchedulingRequest.MaxAvailableMinutes
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