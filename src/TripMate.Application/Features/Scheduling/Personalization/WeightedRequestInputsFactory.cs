using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Scheduling.Personalization;

/// <summary>
/// Factory thuần dựng WeightedRequestInputs từ điểm ranking đã chuẩn hóa và cấu hình cá nhân hóa.
/// </summary>
public sealed class WeightedRequestInputsFactory(
    PersonalParamsFactory paramsFactory,
    ITimeFitProviderFactory? timeFitFactory = null,
    IOptions<TimeFitOptions>? timeFitOptions = null,
    ILogger<WeightedRequestInputsFactory>? logger = null)
{
    private readonly PersonalParamsFactory _paramsFactory = paramsFactory
        ?? throw new ArgumentNullException(nameof(paramsFactory));
    private readonly ITimeFitProviderFactory? _timeFitFactory = timeFitFactory;
    private readonly IOptions<TimeFitOptions>? _timeFitOptions = timeFitOptions;
    private readonly ILogger<WeightedRequestInputsFactory>? _logger = logger;

    public WeightedRequestInputs Create(
        IReadOnlyList<GenerationCandidate> candidates,
        TravelerProfile? profile,
        GenerationInput input,
        PoiRankingOutcomeCategory? rankingOutcome = null,
        ITimeFitProviderFactory? customTimeFit = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(input);

        var baseByPoiId = new Dictionary<long, double>(candidates.Count);

        foreach (var c in candidates)
        {
            double baseScore;
            if (c.EffectiveDesirabilityScore.HasValue)
            {
                baseScore = (double)c.EffectiveDesirabilityScore.Value;
            }
            else if (c.TripMateBaseScore.HasValue)
            {
                baseScore = (double)c.TripMateBaseScore.Value;
            }
            else
            {
                baseScore = 0.5;
                _logger?.LogDebug(
                    "Candidate POI {PoiId} ({PoiName}) missing both EffectiveDesirabilityScore and TripMateBaseScore, defaulting to 0.5.",
                    c.Id,
                    c.Name);
            }

            Invariant.Check(
                baseScore is >= 0.0 and <= 1.0,
                $"Base score for POI {c.Id} must be in [0, 1], got {baseScore}.");

            baseByPoiId[c.Id] = baseScore;
        }

        // Bắt buộc: Mọi ứng viên có thể trở thành node (mọi ứng viên trừ rest candidate) phải có giá trị trong BaseByPoiId.
        foreach (var c in candidates)
        {
            if (!ItineraryScheduleEvaluator.IsQualifiedRestCandidate(c))
            {
                Invariant.Check(
                    baseByPoiId.ContainsKey(c.Id),
                    $"Node candidate POI {c.Id} must have an entry in BaseByPoiId.");
            }
        }

        var personalParams = _paramsFactory.Create(profile);

        var timeFit = customTimeFit
            ?? _timeFitFactory
            ?? new RealTimeFitProviderFactory(personalParams, _timeFitOptions?.Value);

        var baseSource = ResolveBaseSource(candidates, rankingOutcome);

        return new WeightedRequestInputs(
            baseByPoiId,
            personalParams,
            timeFit,
            baseSource);
    }

    public static string ResolveBaseSource(
        IReadOnlyList<GenerationCandidate> candidates,
        PoiRankingOutcomeCategory? rankingOutcome)
    {
        if (rankingOutcome.HasValue)
        {
            return rankingOutcome.Value switch
            {
                PoiRankingOutcomeCategory.Success => "ai-blend",
                PoiRankingOutcomeCategory.ProviderSkipped => "base-only",
                PoiRankingOutcomeCategory.Timeout => "base-fallback:timeout",
                PoiRankingOutcomeCategory.Quota => "base-fallback:quota",
                PoiRankingOutcomeCategory.Network => "base-fallback:network",
                PoiRankingOutcomeCategory.ServerError => "base-fallback:server-error",
                PoiRankingOutcomeCategory.InvalidResponse => "base-fallback:invalid-response",
                _ => $"base-fallback:{rankingOutcome.Value.ToString().ToLowerInvariant()}"
            };
        }

        var hasAiBlendedCandidate = candidates.Any(c =>
            c.EffectiveDesirabilityScore.HasValue
            && c.TripMateBaseScore.HasValue
            && Math.Abs(c.EffectiveDesirabilityScore.Value - c.TripMateBaseScore.Value) > 0.0001m);

        return hasAiBlendedCandidate ? "ai-blend" : "base-only";
    }
}
