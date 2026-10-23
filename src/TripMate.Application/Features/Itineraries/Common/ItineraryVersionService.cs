using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Geo;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Common;

public sealed class ItineraryVersionService(
    IApplicationDbContext dbContext,
    IRouteDurationProvider routeDurationProvider,
    SchedulingGenerationOptions? generationOptions = null,
    TimeOfDayOptions? timeOfDayOptions = null,
    WeightedRequestInputsFactory? weightedInputsFactory = null)
    : IItineraryVersionService
{
    public async Task<Result<Itinerary>> CreateRegeneratedVersionAsync(
        Itinerary source,
        SchedulingRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TransportMode == TransportMode.PublicTransit)
        {
            return UnsupportedPublicTransit();
        }

        var effectiveGenerationOptions = generationOptions ?? new SchedulingGenerationOptions();
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        var travelerProfile = await dbContext.TravelerProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(profile => profile.UserId == request.TravelerUserId, cancellationToken);
        var preferenceTokens = TravelerPreferenceScoring.ParsePreferenceTokens(travelerProfile?.InterestTagsJson);
        var mandatoryIds = JsonSerializer.Deserialize<long[]>(request.MandatoryPoiIdsJson) ?? [];
        var pois = await LoadRelevantActivePoisAsync(request, mandatoryIds, cancellationToken);
        var currentStops = await dbContext.Itineraries
            .AsNoTracking()
            .Where(itinerary => itinerary.Id == source.Id)
            .SelectMany(itinerary => itinerary.Items)
            .OrderBy(item => item.SequenceNo)
            .Select(item => new { item.Kind, item.PointOfInterestId })
            .ToListAsync(cancellationToken);
        // Optional places already suggested in this series, with the latest version that had them.
        var lastSuggestedVersions = await dbContext.Itineraries
            .AsNoTracking()
            .Where(itinerary => itinerary.SchedulingRequestId == request.Id
                && itinerary.Version <= source.Version)
            .SelectMany(
                itinerary => itinerary.Items.Where(item => item.Kind == ItineraryItemKind.Visit
                    && item.PointOfInterestId != null),
                (itinerary, item) => new { PoiId = item.PointOfInterestId!.Value, itinerary.Version })
            .GroupBy(entry => entry.PoiId)
            .Select(group => new { PoiId = group.Key, Version = group.Max(entry => entry.Version) })
            .ToDictionaryAsync(entry => entry.PoiId, entry => entry.Version, cancellationToken);
        foreach (var mandatoryId in mandatoryIds)
        {
            lastSuggestedVersions.Remove(mandatoryId);
        }

        var endPoi = request.EndPointOfInterestId.HasValue
            ? pois.SingleOrDefault(poi => poi.Id == request.EndPointOfInterestId.Value)
            : null;
        if (request.EndPointOfInterestId.HasValue && endPoi is null)
        {
            return Result.Failure<Itinerary>(
                ItineraryErrorCodes.ConstraintsInfeasible,
                "The saved ending location is no longer available.");
        }

        var candidates = LimitMatrixCandidates(PreferOtherPlaces(pois
            .Where(PlanningPoiEligibility.IsPlanningReady)
            .Where(PlanningPoiEligibility.IsAutoSchedulable)
            .Where(poi => GeoDistance.EquirectangularKilometers(
                request.ExplorationLatitude,
                request.ExplorationLongitude,
                poi.Latitude,
                poi.Longitude) <= request.SearchRadiusKm)
            .Select(poi => new GenerationCandidate(
                poi.Id,
                poi.Name,
                new RoutePoint(poi.Latitude, poi.Longitude),
                poi.AverageVisitDurationMinutes,
                poi.EstimatedVisitCost,
                poi.OpeningHours
                    .Where(hour => !hour.IsClosed && hour.OpenTime.HasValue && hour.CloseTime.HasValue)
                    .Select(hour => new GenerationOpeningHours(
                        hour.DayOfWeek,
                        hour.OpenTime!.Value,
                        hour.CloseTime!.Value)).ToArray(),
                TripMateBaseScore: TravelerPreferenceScoring.CalculatePreferenceScore(
                    poi,
                    preferenceTokens),
                EffectiveDesirabilityScore: TravelerPreferenceScoring.CalculatePreferenceScore(
                    poi,
                    preferenceTokens),
                ScenicScoreForRanking: poi.ScenicScore,
                PhotoRatingForRanking: poi.PhotoRating,
                EstimatedVisitCostForRanking: poi.EstimatedVisitCost,
                PreferenceScore: TravelerPreferenceScoring.CalculatePreferenceScore(poi, preferenceTokens),
                ScenicScore: poi.ScenicScore,
                PhotoRating: poi.PhotoRating,
                CategoryName: poi.Category.Name,
                HasShelter: poi.HasShelter)).ToArray(), lastSuggestedVersions), mandatoryIds);
        if (effectiveGenerationOptions.Csp.Mode == ObjectiveMode.WeightedUtility)
        {
            candidates = NormalizeEffectiveDesirabilityScores(candidates);
        }

        var end = endPoi is null
            ? new RoutePoint(request.StartLatitude, request.StartLongitude)
            : new RoutePoint(endPoi.Latitude, endPoi.Longitude);
        var input = new GenerationInput(
            request.StartAtUtc,
            timeZone,
            new RoutePoint(request.StartLatitude, request.StartLongitude),
            end,
            request.AvailableMinutes,
            request.TransportMode,
            request.RestPreference,
            request.BudgetVnd,
            candidates,
            mandatoryIds);
        var routing = new MemoizingRouteDurationProvider(routeDurationProvider);
        WeightedRequestInputs? weightedInputs = null;
        if (effectiveGenerationOptions.Csp.Mode == ObjectiveMode.WeightedUtility)
        {
            var factory = weightedInputsFactory ?? new WeightedRequestInputsFactory(new PersonalParamsFactory());
            weightedInputs = factory.Create(
                input.Candidates,
                travelerProfile,
                input,
                PoiRankingOutcomeCategory.ProviderSkipped);
        }

        var plan = await new ItineraryGenerationService(routing, effectiveGenerationOptions)
            .GenerateAsync(input, weightedInputs, cancellationToken);
        if (plan.IsFailure)
        {
            return Result.Failure<Itinerary>(
                ItineraryErrorCodes.ConstraintsInfeasible,
                plan.ErrorMessage ?? "The saved constraints cannot produce an itinerary.");
        }

        plan = Result.Success(await new TimeOfDayRefiner(routing, effectiveGenerationOptions, timeOfDayOptions ?? new TimeOfDayOptions())
            .RefineAsync(input, plan.Value, TimeOfDayTraitsFactory.From(pois), cancellationToken));

        // Nothing else fits: keep the current version instead of saving an identical copy.
        if (plan.Value.Items
            .OrderBy(item => item.SequenceNo)
            .Select(item => (item.Kind, item.PointOfInterestId))
            .SequenceEqual(currentStops.Select(stop => (stop.Kind, stop.PointOfInterestId))))
        {
            return Result.Failure<Itinerary>(
                ItineraryErrorCodes.NoAlternative,
                "No other places fit this trip's time, area and budget. The current itinerary is already the best option.");
        }

        var title = source.Title ?? $"Generated itinerary - {request.StartAtUtc:dd MMM yyyy}";
        if (title.Length > Itinerary.TitleMaxLength)
        {
            title = title[..Itinerary.TitleMaxLength];
        }

        var successor = Itinerary.CreateCspGenerated(
            request,
            title,
            request.StartAtUtc,
            plan.Value.EndAtUtc,
            source.Version + 1);
        foreach (var item in plan.Value.Items)
        {
            successor.AddItem(GeneratedItineraryItemMapper.ToEntity(item, item.IsMandatory));
        }

        dbContext.Itineraries.Add(successor);
        return Result.Success(successor);
    }

    public Task<Result<Itinerary>> CreateAdjustedVersionAsync(
        Itinerary source,
        SchedulingRequest request,
        IReadOnlyList<long> orderedVisitPoiIds,
        CancellationToken cancellationToken) =>
        CreateAdjustedVersionAsync(
            source,
            request,
            orderedVisitPoiIds.Select(id => new ItineraryStop(id)).ToArray(),
            cancellationToken);

    public async Task<Result<Itinerary>> CreateAdjustedVersionAsync(
        Itinerary source,
        SchedulingRequest request,
        IReadOnlyList<ItineraryStop> stops,
        CancellationToken cancellationToken)
    {
        var evaluations = await EvaluateStopVariantsAsync(request, [stops], cancellationToken);
        if (evaluations.IsFailure)
        {
            return Result.Failure<Itinerary>(evaluations.ErrorCode!, evaluations.ErrorMessage!);
        }

        var evaluation = evaluations.Value[0];
        if (evaluation.Infeasibility is not null)
        {
            if (evaluation.Infeasibility.Reason == StopInfeasibilityReasons.ExceedsEndTime)
            {
                var relaxedEvaluations = await EvaluateStopVariantsAsync(
                    request,
                    [stops],
                    cancellationToken,
                    availableMinutesOverride: SchedulingRequest.MaxAvailableMinutes);

                if (relaxedEvaluations.IsSuccess && relaxedEvaluations.Value[0].Plan is not null)
                {
                    evaluation = relaxedEvaluations.Value[0];
                }
                else
                {
                    return StopInfeasible(evaluation.Infeasibility);
                }
            }
            else
            {
                return StopInfeasible(evaluation.Infeasibility);
            }
        }

        if (evaluation.Plan is null)
        {
            return Result.Failure<Itinerary>(
                ItineraryErrorCodes.ConstraintsInfeasible,
                evaluation.FailureMessage ?? "The selected order cannot produce an itinerary.");
        }

        var mandatoryIds = JsonSerializer.Deserialize<long[]>(request.MandatoryPoiIdsJson) ?? [];
        var plan = evaluation.Plan;

        var title = source.Title ?? $"Generated itinerary - {request.StartAtUtc:dd MMM yyyy}";
        if (title.Length > Itinerary.TitleMaxLength)
        {
            title = title[..Itinerary.TitleMaxLength];
        }

        var successor = Itinerary.CreateCspGenerated(
            request,
            title,
            request.StartAtUtc,
            plan.EndAtUtc,
            source.Version + 1);
        foreach (var item in plan.Items)
        {
            successor.AddItem(GeneratedItineraryItemMapper.ToEntity(
                item,
                item.PointOfInterestId is { } poiId && mandatoryIds.Contains(poiId)));
        }

        dbContext.Itineraries.Add(successor);
        return Result.Success(successor);
    }


    /// <summary>
    /// Schedules each ordered list of stops with the same matrix (one routing call per transport mode) and
    /// without saving anything. Used to save an adjusted version and to check suggestions before showing them.
    /// </summary>
    public async Task<Result<IReadOnlyList<StopsEvaluation>>> EvaluateStopVariantsAsync(
        SchedulingRequest request,
        IReadOnlyList<IReadOnlyList<ItineraryStop>> variants,
        CancellationToken cancellationToken,
        int? availableMinutesOverride = null)
    {
        if (request.TransportMode == TransportMode.PublicTransit)
        {
            return Result.Failure<IReadOnlyList<StopsEvaluation>>(
                ItineraryErrorCodes.ConstraintsInfeasible,
                UnsupportedPublicTransitMessage);
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        var travelerInterestTags = await dbContext.TravelerProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == request.TravelerUserId)
            .Select(profile => profile.InterestTagsJson)
            .SingleOrDefaultAsync(cancellationToken);
        var preferenceTokens = TravelerPreferenceScoring.ParsePreferenceTokens(travelerInterestTags);
        var mandatoryIds = JsonSerializer.Deserialize<long[]>(request.MandatoryPoiIdsJson) ?? [];
        var pois = await LoadRelevantActivePoisAsync(request, mandatoryIds, cancellationToken);
        var endPoi = request.EndPointOfInterestId.HasValue
            ? pois.SingleOrDefault(poi => poi.Id == request.EndPointOfInterestId.Value)
            : null;
        if (request.EndPointOfInterestId.HasValue && endPoi is null)
        {
            return Result.Failure<IReadOnlyList<StopsEvaluation>>(
                ItineraryErrorCodes.ConstraintsInfeasible,
                "The saved ending location is no longer available.");
        }

        // Every chosen stop of every variant stays in the matrix, so all variants share one matrix.
        var keepIds = mandatoryIds
            .Concat(variants.SelectMany(variant => variant.Select(stop => stop.PoiId)))
            .Distinct()
            .ToArray();
        var candidates = LimitMatrixCandidates(pois
            .Where(PlanningPoiEligibility.IsPlanningReady)
            .Where(poi => GeoDistance.EquirectangularKilometers(
                request.ExplorationLatitude,
                request.ExplorationLongitude,
                poi.Latitude,
                poi.Longitude) <= request.SearchRadiusKm)
            .Select(poi => new GenerationCandidate(
                poi.Id,
                poi.Name,
                new RoutePoint(poi.Latitude, poi.Longitude),
                poi.AverageVisitDurationMinutes,
                poi.EstimatedVisitCost,
                poi.OpeningHours
                    .Where(hour => !hour.IsClosed && hour.OpenTime.HasValue && hour.CloseTime.HasValue)
                    .Select(hour => new GenerationOpeningHours(
                        hour.DayOfWeek,
                        hour.OpenTime!.Value,
                        hour.CloseTime!.Value)).ToArray(),
                TripMateBaseScore: TravelerPreferenceScoring.CalculatePreferenceScore(
                    poi,
                    preferenceTokens),
                EffectiveDesirabilityScore: TravelerPreferenceScoring.CalculatePreferenceScore(
                    poi,
                    preferenceTokens),
                ScenicScoreForRanking: poi.ScenicScore,
                PhotoRatingForRanking: poi.PhotoRating,
                EstimatedVisitCostForRanking: poi.EstimatedVisitCost,
                PreferenceScore: TravelerPreferenceScoring.CalculatePreferenceScore(poi, preferenceTokens),
                ScenicScore: poi.ScenicScore,
                PhotoRating: poi.PhotoRating,
                CategoryName: poi.Category.Name,
                HasShelter: poi.HasShelter)), keepIds);
        var effectiveAvailableMinutes = availableMinutesOverride ?? request.AvailableMinutes;
        var input = new GenerationInput(
            request.StartAtUtc,
            timeZone,
            new RoutePoint(request.StartLatitude, request.StartLongitude),
            endPoi is null
                ? new RoutePoint(request.StartLatitude, request.StartLongitude)
                : new RoutePoint(endPoi.Latitude, endPoi.Longitude),
            effectiveAvailableMinutes,
            request.TransportMode,
            request.RestPreference,
            request.BudgetVnd,
            candidates,
            mandatoryIds);

        var sharedRouting = new MemoizingRouteDurationProvider(routeDurationProvider);
        var evaluations = new List<StopsEvaluation>(variants.Count);
        foreach (var stops in variants)
        {
            evaluations.Add(await EvaluateStopsAsync(input, mandatoryIds, stops, sharedRouting, cancellationToken));
        }

        return Result.Success<IReadOnlyList<StopsEvaluation>>(evaluations);
    }

    private async Task<StopsEvaluation> EvaluateStopsAsync(
        GenerationInput input,
        IReadOnlyCollection<long> mandatoryIds,
        IReadOnlyList<ItineraryStop> stops,
        IRouteDurationProvider sharedRouting,
        CancellationToken cancellationToken)
    {
        var orderedVisitPoiIds = stops.Select(stop => stop.PoiId).ToArray();
        var unavailable = await FindUnavailableStopAsync(orderedVisitPoiIds, input.Candidates, cancellationToken);
        if (unavailable is not null)
        {
            return StopsEvaluation.Breaks(unavailable);
        }

        // A mandatory place the traveler removed or replaced in this variant stops being mandatory, exactly as
        // saving the adjustment does (SchedulingRequest.KeepOnlyMandatoryPois).
        var keptMandatory = mandatoryIds.Where(orderedVisitPoiIds.Contains).ToArray();
        input = input with { MandatoryPoiIds = keptMandatory };

        var candidateLocations = input.Candidates.ToDictionary(candidate => candidate.Id, candidate => candidate.Location);
        var legs = stops.Zip(stops.Skip(1), (from, to) => (from, to))
            .Where(leg => leg.from.TransportModeToNext.HasValue)
            .ToArray();
        var routing = new MemoizingRouteDurationProvider(new LegModeRouteDurationProvider(
            sharedRouting,
            legs.Select(leg => new LegModeOverride(
                    candidateLocations[leg.from.PoiId],
                    candidateLocations[leg.to.PoiId],
                    leg.from.TransportModeToNext!.Value))
                .ToArray(),
            stops.Select(stop => candidateLocations[stop.PoiId]).ToArray()));
        var plan = await new ItineraryGenerationService(routing, generationOptions)
            .GenerateFixedOrderAsync(input, orderedVisitPoiIds, cancellationToken);
        if (plan.IsFailure)
        {
            // Tier 1: Elastic Visit Duration Compression
            var deadline = input.StartAtUtc.AddMinutes(input.AvailableMinutes);
            var relaxedInput = input with { AvailableMinutes = SchedulingRequest.MaxAvailableMinutes };
            var relaxedPlan = await new ItineraryGenerationService(routing, generationOptions)
                .GenerateFixedOrderAsync(relaxedInput, orderedVisitPoiIds, cancellationToken);

            var absorbed = false;
            if (relaxedPlan.IsSuccess && relaxedPlan.Value.EndAtUtc > deadline)
            {
                var overflowMinutes = (int)Math.Ceiling((relaxedPlan.Value.EndAtUtc - deadline).TotalMinutes);
                var proportionalInput = ElasticVisitCompression.ApplyCompression(input, orderedVisitPoiIds, overflowMinutes);
                var proportionalPlan = await new ItineraryGenerationService(routing, generationOptions)
                    .GenerateFixedOrderAsync(proportionalInput, orderedVisitPoiIds, cancellationToken);

                if (proportionalPlan.IsSuccess)
                {
                    plan = proportionalPlan;
                    absorbed = true;
                }
            }

            if (!absorbed)
            {
                var fullCompressedInput = ElasticVisitCompression.ApplyCompression(input, orderedVisitPoiIds);
                var fullCompressedPlan = await new ItineraryGenerationService(routing, generationOptions)
                    .GenerateFixedOrderAsync(fullCompressedInput, orderedVisitPoiIds, cancellationToken);

                if (fullCompressedPlan.IsSuccess)
                {
                    plan = fullCompressedPlan;
                    absorbed = true;
                }
            }

            if (!absorbed)
            {
                var fullCompressedInput = ElasticVisitCompression.ApplyCompression(input, orderedVisitPoiIds);
                var explanation = await new FixedOrderDiagnosis(routing, generationOptions)
                    .ExplainAsync(fullCompressedInput, orderedVisitPoiIds, cancellationToken);
                return explanation is not null
                    ? StopsEvaluation.Breaks(explanation)
                    : StopsEvaluation.Failed(plan.ErrorMessage ?? "The selected order cannot produce an itinerary.");
            }
        }

        // Label the legs whose transport the traveler chose (their matrix cells were already swapped).
        var chosenModes = legs.ToDictionary(
            leg => (leg.from.PoiId, leg.to.PoiId),
            leg => leg.from.TransportModeToNext!.Value);
        var planItems = ChosenLegModes.Label(plan.Value.Items.OrderBy(item => item.SequenceNo).ToArray(), chosenModes);

        return StopsEvaluation.Success(plan.Value with { Items = planItems });
    }
    /// <summary>
    /// Regenerating asks for another option. Places never suggested in this series rank first,
    /// then places suggested longest ago, and the current version's places last; within a tier the
    /// usual ranking holds. The solver always prefers higher-ranked places, so each regeneration
    /// explores places not shown yet and reuses earlier ones only to fill time that is left.
    /// </summary>
    private static IEnumerable<GenerationCandidate> PreferOtherPlaces(
        IReadOnlyList<GenerationCandidate> candidates,
        IReadOnlyDictionary<long, int> lastSuggestedVersions)
    {
        if (candidates.Count == 0 || lastSuggestedVersions.Count == 0)
        {
            return candidates;
        }

        // Larger than the whole score spread, so a tier never overtakes a better one.
        var tierStep = candidates.Max(candidate => candidate.EffectiveDesirabilityScore ?? 0m)
            - candidates.Min(candidate => candidate.EffectiveDesirabilityScore ?? 0m)
            + 1m;
        return candidates.Select(candidate => lastSuggestedVersions.TryGetValue(candidate.Id, out var version)
            ? candidate with { EffectiveDesirabilityScore = (candidate.EffectiveDesirabilityScore ?? 0m) - (tierStep * version) }
            : candidate);
    }

    private static IReadOnlyList<GenerationCandidate> NormalizeEffectiveDesirabilityScores(
        IReadOnlyList<GenerationCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var scores = candidates
            .Select(candidate => candidate.EffectiveDesirabilityScore
                ?? candidate.TripMateBaseScore
                ?? 0.5m)
            .ToArray();
        var minimum = scores.Min();
        var maximum = scores.Max();
        if (minimum >= 0m && maximum <= 1m)
        {
            return candidates;
        }

        var range = maximum - minimum;
        return candidates
            .Select((candidate, index) => candidate with
            {
                EffectiveDesirabilityScore = range == 0m
                    ? Math.Clamp(scores[index], 0m, 1m)
                    : (scores[index] - minimum) / range,
            })
            .ToArray();
    }

    private IReadOnlyList<GenerationCandidate> LimitMatrixCandidates(
        IEnumerable<GenerationCandidate> candidates,
        IReadOnlyCollection<long> mandatoryPoiIds)
    {
        var mandatoryIds = mandatoryPoiIds.ToHashSet();
        var materialized = candidates.ToArray();
        var mandatory = materialized
            .Where(candidate => mandatoryIds.Contains(candidate.Id))
            .OrderBy(candidate => candidate.Id);
        var optional = materialized
            .Where(candidate => !mandatoryIds.Contains(candidate.Id))
            .OrderByDescending(candidate => candidate.EffectiveDesirabilityScore ?? decimal.MinValue)
            .ThenByDescending(candidate => candidate.ScenicScoreForRanking ?? decimal.MinValue)
            .ThenByDescending(candidate => candidate.PhotoRatingForRanking ?? decimal.MinValue)
            .ThenBy(candidate => candidate.EstimatedVisitCostForRanking ?? decimal.MaxValue)
            .ThenBy(candidate => candidate.Id)
            .Take(Math.Max(0, (generationOptions ?? new SchedulingGenerationOptions())
                .EffectiveMaxMatrixCandidates - mandatoryIds.Count));
        return mandatory.Concat(optional).ToArray();
    }

    private async Task<List<PointOfInterest>> LoadRelevantActivePoisAsync(
        SchedulingRequest request,
        IReadOnlyCollection<long> mandatoryIds,
        CancellationToken cancellationToken)
        => await dbContext.PointsOfInterest
            .WithPlanningDetails(
                request.ExplorationLatitude,
                request.ExplorationLongitude,
                request.SearchRadiusKm,
                mandatoryIds,
                request.EndPointOfInterestId)
            .ToListAsync(cancellationToken);

    private const string UnsupportedPublicTransitMessage =
        "Public transit routing is not available yet. Choose walking, motorbike, or car.";

    private static Result<Itinerary> UnsupportedPublicTransit() => Result.Failure<Itinerary>(
        ItineraryErrorCodes.ConstraintsInfeasible,
        UnsupportedPublicTransitMessage);

    private static Result<Itinerary> StopInfeasible(StopInfeasibility explanation) => Result.Failure<Itinerary>(
        ItineraryErrorCodes.StopInfeasible,
        "A selected location does not fit the itinerary.",
        explanation.ToErrorMetadata());

    /// <summary>
    /// A chosen stop that generation cannot use at all: unknown or inactive, without known opening hours or
    /// planning data, or outside the trip's search area.
    /// </summary>
    private async Task<StopInfeasibility?> FindUnavailableStopAsync(
        IReadOnlyList<long> orderedVisitPoiIds,
        IEnumerable<GenerationCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var candidateIds = candidates.Select(candidate => candidate.Id).ToHashSet();
        var missingIds = orderedVisitPoiIds.Where(id => !candidateIds.Contains(id)).ToArray();
        if (missingIds.Length == 0)
        {
            return null;
        }

        var missing = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Include(poi => poi.OpeningHours)
            .Where(poi => missingIds.Contains(poi.Id))
            .ToDictionaryAsync(poi => poi.Id, cancellationToken);
        var poiId = missingIds[0];
        if (!missing.TryGetValue(poiId, out var poi) || poi.Status != PointOfInterestStatus.Active)
        {
            return new StopInfeasibility(poiId, StopInfeasibilityReasons.Inactive);
        }

        return PlanningPoiEligibility.IsPlanningReady(poi)
            ? new StopInfeasibility(poiId, StopInfeasibilityReasons.OutsideRadius)
            : new StopInfeasibility(poiId, StopInfeasibilityReasons.NoOpeningHours);
    }
}