using System.Diagnostics;
using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Shadow;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services;

/// <summary>
/// Background runner xử lý các công việc chạy shadow chế độ WeightedUtility không đồng bộ qua hàng đợi Channel.
/// Chạy cô lập, có giới hạn thời gian (TimeoutMs), ghi nhận metrics có cấu trúc và không ảnh hưởng luồng request chính.
/// </summary>
public class ShadowSchedulingRunner : BackgroundService, IShadowSchedulingRunner
{
    private const int DefaultChannelCapacity = 100;
    private readonly Channel<ShadowWorkItem> _channel;
    private readonly IOptions<ShadowOptions> _options;
    private readonly WeightedRequestInputsFactory _weightedInputsFactory;
    private readonly ILogger<ShadowSchedulingRunner>? _logger;

    public ShadowSchedulingRunner(
        IOptions<ShadowOptions> options,
        WeightedRequestInputsFactory? weightedInputsFactory = null,
        ILogger<ShadowSchedulingRunner>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _weightedInputsFactory = weightedInputsFactory ?? new WeightedRequestInputsFactory(new PersonalParamsFactory());
        _logger = logger;
        _channel = Channel.CreateBounded<ShadowWorkItem>(new BoundedChannelOptions(DefaultChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Enqueue(ShadowWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_options.Value.Enabled)
        {
            return;
        }

        if (!_channel.Writer.TryWrite(item))
        {
            _logger?.LogWarning("Shadow runner queue is full. Dropped shadow execution for Request {RequestId}.", item.RequestId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessItemAsync(item, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Unexpected error in shadow runner while processing Request {RequestId}.", item.RequestId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown
        }
    }

    public async Task<ShadowMetrics> ProcessItemAsync(ShadowWorkItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var timeoutMs = Math.Max(100, _options.Value.TimeoutMs);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);

        WeightedRequestInputs weightedInputs;
        try
        {
            weightedInputs = _weightedInputsFactory.Create(
                item.Input.Candidates,
                item.TravelerProfile,
                item.Input,
                item.RankingOutcome);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create WeightedRequestInputs for shadow Request {RequestId}.", item.RequestId);
            return new ShadowMetrics(
                item.RequestId,
                Success: false,
                TimedOut: false,
                ElapsedMs: 0,
                ModelObjective: null,
                IsSequenceIdentical: false,
                JaccardSimilarity: 0,
                AddedStopsCount: 0,
                RemovedStopsCount: 0,
                DurationDiffMinutes: 0,
                CostDiffVnd: 0,
                Error: ex.Message);
        }

        var shadowGenOptions = new SchedulingGenerationOptions
        {
            SolverMode = SchedulingSolverMode.Csp,
            EnableOptionalRouteOptimization = item.GenerationOptions.EnableOptionalRouteOptimization,
            MaxMatrixCandidates = item.GenerationOptions.MaxMatrixCandidates,
            MaxRouteOptimizationSeeds = item.GenerationOptions.MaxRouteOptimizationSeeds,
            MaxRouteEvaluations = item.GenerationOptions.MaxRouteEvaluations,
            TransitionBufferMinutes = item.GenerationOptions.TransitionBufferMinutes,
            FinalReturnBufferMinutes = item.GenerationOptions.FinalReturnBufferMinutes,
            MiniRouting = item.GenerationOptions.MiniRouting,
            Csp = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                MaxNodes = item.GenerationOptions.Csp.WeightedMaxNodes ?? item.GenerationOptions.Csp.MaxNodes,
                TimeLimitMilliseconds = Math.Min(item.GenerationOptions.Csp.TimeLimitMilliseconds, timeoutMs),
                FinalCandidatesToValidate = item.GenerationOptions.Csp.FinalCandidatesToValidate,
                PolishWithLocalSearch = false,
            }
        };

        var sw = Stopwatch.StartNew();
        Result<GeneratedItineraryPlan> shadowPlanResult;
        bool timedOut = false;
        try
        {
            var service = new ItineraryGenerationService(
                new PrecomputedRouteDurationProvider(item.RouteDurationMatrix),
                shadowGenOptions);
            shadowPlanResult = await service.GenerateAsync(item.Input, weightedInputs, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            shadowPlanResult = Result.Failure<GeneratedItineraryPlan>("planning.timeout", "Shadow execution timed out.");
        }
        catch (Exception ex)
        {
            shadowPlanResult = Result.Failure<GeneratedItineraryPlan>("planning.error", ex.Message);
        }
        sw.Stop();

        if (!shadowPlanResult.IsSuccess)
        {
            var error = shadowPlanResult.ErrorMessage ?? (timedOut ? "Timeout" : "Unknown error");
            _logger?.LogInformation(
                "Shadow run finished for Request {RequestId} in {ElapsedMs:F1}ms. Success: False, TimedOut: {TimedOut}, Error: {Error}",
                item.RequestId,
                sw.Elapsed.TotalMilliseconds,
                timedOut,
                error);

            return new ShadowMetrics(
                item.RequestId,
                Success: false,
                TimedOut: timedOut,
                ElapsedMs: sw.Elapsed.TotalMilliseconds,
                ModelObjective: null,
                IsSequenceIdentical: false,
                JaccardSimilarity: 0,
                AddedStopsCount: 0,
                RemovedStopsCount: 0,
                DurationDiffMinutes: 0,
                CostDiffVnd: 0,
                Error: error);
        }

        var primaryVisits = item.PrimaryPlan.Items
            .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
            .OrderBy(i => i.SequenceNo)
            .Select(i => i.PointOfInterestId!.Value)
            .ToList();

        var shadowVisits = shadowPlanResult.Value.Items
            .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
            .OrderBy(i => i.SequenceNo)
            .Select(i => i.PointOfInterestId!.Value)
            .ToList();

        bool isIdentical = primaryVisits.SequenceEqual(shadowVisits);
        var primarySet = primaryVisits.ToHashSet();
        var shadowSet = shadowVisits.ToHashSet();

        int unionCount = primarySet.Union(shadowSet).Count();
        int intersectCount = primarySet.Intersect(shadowSet).Count();
        double jaccard = unionCount == 0 ? 1.0 : (double)intersectCount / unionCount;

        int added = shadowSet.Except(primarySet).Count();
        int removed = primarySet.Except(shadowSet).Count();
        int durationDiff = shadowPlanResult.Value.TotalDurationMinutes - item.PrimaryPlan.TotalDurationMinutes;
        decimal costDiff = shadowPlanResult.Value.TotalEstimatedCost - item.PrimaryPlan.TotalEstimatedCost;

        long? modelObjective = shadowPlanResult.Value.ModelObjective;

        _logger?.LogInformation(
            "Shadow run finished for Request {RequestId} in {ElapsedMs:F1}ms. Success: True, J_model: {J_model}, Identical: {IsIdentical}, Jaccard: {Jaccard:F2}, PrimaryStops: {PrimaryStops}, ShadowStops: {ShadowStops}, DurationDiff: {DurationDiff}min, CostDiff: {CostDiff:N0}VND.",
            item.RequestId,
            sw.Elapsed.TotalMilliseconds,
            modelObjective,
            isIdentical,
            jaccard,
            primaryVisits.Count,
            shadowVisits.Count,
            durationDiff,
            costDiff);

        return new ShadowMetrics(
            item.RequestId,
            Success: true,
            TimedOut: false,
            ElapsedMs: sw.Elapsed.TotalMilliseconds,
            ModelObjective: modelObjective,
            IsSequenceIdentical: isIdentical,
            JaccardSimilarity: jaccard,
            AddedStopsCount: added,
            RemovedStopsCount: removed,
            DurationDiffMinutes: durationDiff,
            CostDiffVnd: costDiff,
            Error: null);
    }

    private sealed class PrecomputedRouteDurationProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(matrix);
        }
    }
}