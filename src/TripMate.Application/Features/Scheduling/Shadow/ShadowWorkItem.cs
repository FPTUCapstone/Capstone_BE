using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Scheduling.Shadow;

/// <summary>
/// Gói công việc chạy ngầm (shadow execution) cho chế độ WeightedUtility đối chứng với Lexicographic.
/// </summary>
public sealed record ShadowWorkItem(
    long RequestId,
    GenerationInput Input,
    IRouteDurationProvider RouteDurationProvider,
    SchedulingGenerationOptions GenerationOptions,
    TravelerProfile? TravelerProfile,
    PoiRankingOutcomeCategory? RankingOutcome,
    GeneratedItineraryPlan PrimaryPlan);