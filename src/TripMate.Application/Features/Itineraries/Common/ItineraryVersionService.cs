using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Geo;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Common;

public sealed class ItineraryVersionService(
    IApplicationDbContext dbContext,
    IRouteDurationProvider routeDurationProvider,
    SchedulingGenerationOptions? generationOptions = null)
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

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        var travelerInterestTags = await dbContext.TravelerProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == request.TravelerUserId)
            .Select(profile => profile.InterestTagsJson)
            .SingleOrDefaultAsync(cancellationToken);
        var preferenceTokens = TravelerPreferenceScoring.ParsePreferenceTokens(travelerInterestTags);
        var pois = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Include(poi => poi.Category)
            .Include(poi => poi.OpeningHours)
            .Include(poi => poi.PoiTags)
            .ThenInclude(mapping => mapping.Tag)
            .Where(poi => poi.Status == PointOfInterestStatus.Active)
            .ToListAsync(cancellationToken);

        var endPoi = request.EndPointOfInterestId.HasValue
            ? pois.SingleOrDefault(poi => poi.Id == request.EndPointOfInterestId.Value)
            : null;
        if (request.EndPointOfInterestId.HasValue && endPoi is null)
        {
            return Result.Failure<Itinerary>(
                ItineraryErrorCodes.ConstraintsInfeasible,
                "The saved ending location is no longer available.");
        }

        var mandatoryIds = JsonSerializer.Deserialize<long[]>(request.MandatoryPoiIdsJson) ?? [];
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
                HasShelter: poi.HasShelter)), mandatoryIds);
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
        var plan = await new ItineraryGenerationService(routeDurationProvider, generationOptions)
            .GenerateAsync(input, cancellationToken);
        if (plan.IsFailure)
        {
            return Result.Failure<Itinerary>(
                ItineraryErrorCodes.ConstraintsInfeasible,
                plan.ErrorMessage ?? "The saved constraints cannot produce an itinerary.");
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
            successor.AddItem(item.Kind == ItineraryItemKind.Rest
                ? ItineraryItem.CreateRest(
                    item.SequenceNo,
                    item.PointOfInterestId,
                    item.PlannedArrivalUtc,
                    item.PlannedDepartureUtc,
                    item.RecommendationReason,
                    item.TravelDurationToNextMinutes)
                : ItineraryItem.CreateVisit(
                    item.SequenceNo,
                    item.PointOfInterestId!.Value,
                    item.PlannedArrivalUtc,
                    item.PlannedDepartureUtc,
                    item.IsMandatory,
                    item.EstimatedCost,
                    item.RecommendationReason,
                    item.TravelDurationToNextMinutes));
        }

        dbContext.Itineraries.Add(successor);
        return Result.Success(successor);
    }

    public async Task<Result<Itinerary>> CreateAdjustedVersionAsync(
        Itinerary source,
        SchedulingRequest request,
        IReadOnlyList<long> orderedVisitPoiIds,
        CancellationToken cancellationToken)
    {
        if (request.TransportMode == TransportMode.PublicTransit)
        {
            return UnsupportedPublicTransit();
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        var travelerInterestTags = await dbContext.TravelerProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == request.TravelerUserId)
            .Select(profile => profile.InterestTagsJson)
            .SingleOrDefaultAsync(cancellationToken);
        var preferenceTokens = TravelerPreferenceScoring.ParsePreferenceTokens(travelerInterestTags);
        var pois = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Include(poi => poi.Category)
            .Include(poi => poi.OpeningHours)
            .Include(poi => poi.PoiTags)
            .ThenInclude(mapping => mapping.Tag)
            .Where(poi => poi.Status == PointOfInterestStatus.Active)
            .ToListAsync(cancellationToken);
        var endPoi = request.EndPointOfInterestId.HasValue
            ? pois.SingleOrDefault(poi => poi.Id == request.EndPointOfInterestId.Value)
            : null;
        if (request.EndPointOfInterestId.HasValue && endPoi is null)
        {
            return Result.Failure<Itinerary>(
                ItineraryErrorCodes.ConstraintsInfeasible,
                "The saved ending location is no longer available.");
        }

        var mandatoryIds = JsonSerializer.Deserialize<long[]>(request.MandatoryPoiIdsJson) ?? [];
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
                HasShelter: poi.HasShelter)), mandatoryIds);
        var input = new GenerationInput(
            request.StartAtUtc,
            timeZone,
            new RoutePoint(request.StartLatitude, request.StartLongitude),
            endPoi is null
                ? new RoutePoint(request.StartLatitude, request.StartLongitude)
                : new RoutePoint(endPoi.Latitude, endPoi.Longitude),
            request.AvailableMinutes,
            request.TransportMode,
            request.RestPreference,
            request.BudgetVnd,
            candidates,
            mandatoryIds);
        var plan = await new ItineraryGenerationService(routeDurationProvider, generationOptions)
            .GenerateFixedOrderAsync(input, orderedVisitPoiIds, cancellationToken);
        if (plan.IsFailure)
        {
            return Result.Failure<Itinerary>(
                ItineraryErrorCodes.ConstraintsInfeasible,
                plan.ErrorMessage ?? "The selected order cannot produce an itinerary.");
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
            successor.AddItem(item.Kind == ItineraryItemKind.Rest
                ? ItineraryItem.CreateRest(
                    item.SequenceNo,
                    item.PointOfInterestId,
                    item.PlannedArrivalUtc,
                    item.PlannedDepartureUtc,
                    item.RecommendationReason,
                    item.TravelDurationToNextMinutes)
                : ItineraryItem.CreateVisit(
                    item.SequenceNo,
                    item.PointOfInterestId!.Value,
                    item.PlannedArrivalUtc,
                    item.PlannedDepartureUtc,
                    mandatoryIds.Contains(item.PointOfInterestId.Value),
                    item.EstimatedCost,
                    item.RecommendationReason,
                    item.TravelDurationToNextMinutes));
        }

        dbContext.Itineraries.Add(successor);
        return Result.Success(successor);
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
            .OrderByDescending(candidate => candidate.EffectiveDesirabilityScore)
            .ThenByDescending(candidate => candidate.ScenicScoreForRanking ?? decimal.MinValue)
            .ThenByDescending(candidate => candidate.PhotoRatingForRanking ?? decimal.MinValue)
            .ThenBy(candidate => candidate.EstimatedVisitCostForRanking ?? decimal.MaxValue)
            .ThenBy(candidate => candidate.Id)
            .Take(Math.Max(0, (generationOptions ?? new SchedulingGenerationOptions())
                .EffectiveMaxMatrixCandidates - mandatoryIds.Count));
        return mandatory.Concat(optional).ToArray();
    }

    private static Result<Itinerary> UnsupportedPublicTransit() => Result.Failure<Itinerary>(
        ItineraryErrorCodes.ConstraintsInfeasible,
        "Public transit routing is not available yet. Choose walking, motorbike, or car.");
}
