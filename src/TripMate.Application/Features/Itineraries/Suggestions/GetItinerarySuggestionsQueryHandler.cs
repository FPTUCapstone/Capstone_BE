using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Geo;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.AdjustItems;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.PointsOfInterest.ExternalReviews;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Suggestions;

public sealed class GetItinerarySuggestionsQueryHandler(
    IApplicationDbContext dbContext,
    IItineraryAccessService accessService,
    IItineraryVersionService versionService,
    ITripadvisorClient tripadvisor)
    : IRequestHandler<GetItinerarySuggestionsQuery, Result<ItinerarySuggestionsDto>>
{
    public async Task<Result<ItinerarySuggestionsDto>> Handle(
        GetItinerarySuggestionsQuery query,
        CancellationToken cancellationToken)
    {
        var access = await accessService.ResolveCurrentAsync(
            query.ItineraryId,
            query.TravelerUserId,
            trackCurrent: false,
            cancellationToken);
        if (access.IsFailure)
        {
            return Result.Failure<ItinerarySuggestionsDto>(access.ErrorCode!, access.ErrorMessage!, access.ErrorMetadata);
        }

        if (!access.Value.CanManage)
        {
            return Result.Failure<ItinerarySuggestionsDto>(
                ItineraryErrorCodes.OwnerPermissionRequired,
                "Only the itinerary owner can change its stops.");
        }

        var itinerary = await dbContext.Itineraries
            .AsNoTracking()
            .Include(item => item.Items)
            .ThenInclude(item => item.PointOfInterest)
            .ThenInclude(poi => poi!.Category)
            .SingleAsync(item => item.Id == access.Value.CurrentItinerary.Id, cancellationToken);
        var request = await dbContext.SchedulingRequests
            .AsNoTracking()
            .SingleAsync(item => item.Id == access.Value.SchedulingRequestId, cancellationToken);

        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        var visits = items
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.PointOfInterestId.HasValue)
            .ToArray();
        var currentStops = visits
            .Select((item, index) => new ItineraryStop(
                item.PointOfInterestId!.Value,
                index < visits.Length - 1 ? item.TransportModeToNext : null))
            .ToArray();

        var anchorResult = ResolveAnchor(query, items, visits);
        if (anchorResult.IsFailure)
        {
            return Result.Failure<ItinerarySuggestionsDto>(anchorResult.ErrorCode!, anchorResult.ErrorMessage!);
        }

        var (anchor, insertIndex, replaceIndex, category) = anchorResult.Value;
        if (insertIndex >= 0 && currentStops.Length >= AdjustItineraryItemsCommand.MaxStops)
        {
            // Adding one more stop could not be saved, so offer nothing rather than a dead end.
            return Result.Success(new ItinerarySuggestionsDto(itinerary.Id, query.Kind, request.SearchRadiusKm, []));
        }

        var currentIds = currentStops.Select(stop => stop.PoiId).ToHashSet();
        var places = await LoadPlacesAsync(request, category, currentIds, cancellationToken);
        var anchorPoint = anchor.PointOfInterest is { } anchorPoi
            ? (anchorPoi.Latitude, anchorPoi.Longitude)
            : (request.StartLatitude, request.StartLongitude);
        var nearest = places
            .Select(poi => (Poi: poi, Km: GeoDistance.EquirectangularKilometers(
                anchorPoint.Item1, anchorPoint.Item2, poi.Latitude, poi.Longitude)))
            .OrderBy(entry => PoiCategoryNames.IsFoodAndDrink(category)
                ? PoiCategoryNames.IsFoodAndDrink(entry.Poi.Category?.Name) ? 0 : 1
                : entry.Poi.Category?.Name == category ? 0 : 1)
            .ThenBy(entry => entry.Km)
            .ThenBy(entry => entry.Poi.Id)
            .Take(GetItinerarySuggestionsQuery.CandidatesToEvaluate)
            .ToArray();
        if (nearest.Length == 0)
        {
            return Result.Success(new ItinerarySuggestionsDto(itinerary.Id, query.Kind, request.SearchRadiusKm, []));
        }

        var variants = new List<IReadOnlyList<ItineraryStop>> { currentStops };
        variants.AddRange(nearest.Select(entry => (IReadOnlyList<ItineraryStop>)WithCandidate(
            currentStops, entry.Poi.Id, insertIndex, replaceIndex)));

        var effectiveAvailableMinutes = itinerary.ValidToUtc.HasValue
            ? Math.Max(request.AvailableMinutes, (int)Math.Ceiling((itinerary.ValidToUtc.Value - request.StartAtUtc).TotalMinutes))
            : request.AvailableMinutes;


        IReadOnlyList<StopsEvaluation>? evaluations = null;
        try
        {
            var evaluated = await versionService.EvaluateStopVariantsAsync(
                request,
                variants,
                cancellationToken,
                availableMinutesOverride: effectiveAvailableMinutes);
            if (evaluated.IsSuccess)
            {
                evaluations = evaluated.Value;
            }
        }
        catch (RouteDurationProviderException)
        {
            // Routing is down: fall back to straight-line distance and unknown fit.
        }

        var nearestIds = nearest.Select(entry => entry.Poi.Id).ToArray();
        var thumbnails = (await dbContext.PoiPhotos
                .AsNoTracking()
                .Where(photo => nearestIds.Contains(photo.PointOfInterestId))
                .Select(photo => new { photo.PointOfInterestId, photo.SortOrder, photo.Id, photo.Url })
                .ToListAsync(cancellationToken))
            .GroupBy(photo => photo.PointOfInterestId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(photo => photo.SortOrder).ThenBy(photo => photo.Id).First().Url);
        var baselineEnd = evaluations?[0].Plan?.EndAtUtc ?? itinerary.ValidToUtc;

        var suggestions = nearest
            .Select((entry, index) => ToSuggestion(
                entry.Poi,
                entry.Km,
                thumbnails.GetValueOrDefault(entry.Poi.Id),
                evaluations?[index + 1],
                baselineEnd,
                request))
            .OrderByDescending(suggestion => suggestion.FitsItinerary ?? true)
            .ThenBy(suggestion => suggestion.DetourMinutes ?? int.MaxValue)
            .ThenBy(suggestion => suggestion.DistanceFromPreviousMeters ?? int.MaxValue)
            .Take(GetItinerarySuggestionsQuery.MaxSuggestions)
            .ToArray();

        var linked = nearest
            .Where(entry => entry.Poi.TripadvisorLocationId.HasValue
                && suggestions.Any(suggestion => suggestion.PoiId == entry.Poi.Id))
            .ToDictionary(entry => entry.Poi.Id, entry => entry.Poi.TripadvisorLocationId!.Value);
        if (linked.Count > 0 && tripadvisor.IsEnabled)
        {
            var ratings = new Dictionary<long, TripadvisorRating>(
                await tripadvisor.GetRatingsAsync(linked.Values.ToArray(), cancellationToken));

            // The rating counts every review ever written, but Tripadvisor only serves recent ones; check the
            // (cached, one call per location) recent pool so clients offer "Xem đánh giá" only when there is one.
            foreach (var (locationId, rating) in ratings.ToArray())
            {
                var recent = await tripadvisor.GetReviewsAsync(locationId, 1, cancellationToken);
                ratings[locationId] = rating with { HasRecentReviews = recent.Count > 0 };
            }

            suggestions = suggestions
                .Select(suggestion => linked.TryGetValue(suggestion.PoiId, out var locationId)
                    && ratings.TryGetValue(locationId, out var rating)
                        ? suggestion with { Tripadvisor = rating }
                        : suggestion)
                .ToArray();
        }

        return Result.Success(new ItinerarySuggestionsDto(itinerary.Id, query.Kind, request.SearchRadiusKm, suggestions));
    }

    private static Result<(ItineraryItem Anchor, int InsertIndex, int ReplaceIndex, string? Category)> ResolveAnchor(
        GetItinerarySuggestionsQuery query,
        IReadOnlyList<ItineraryItem> items,
        IReadOnlyList<ItineraryItem> visits)
    {
        if (query.Kind == GetItinerarySuggestionsQuery.KindFood)
        {
            var after = items.FirstOrDefault(item => item.Id == query.AfterItemId);
            if (after is null)
            {
                return NotInItinerary();
            }

            // Insert after the last visit at or before the anchor (a rest item anchors to the visit before it).
            var insertIndex = visits.Count(visit => visit.SequenceNo <= after.SequenceNo);
            return Result.Success((after, insertIndex, -1, (string?)PoiCategoryNames.FoodAndDrink));
        }

        var replaced = visits.FirstOrDefault(item => item.Id == query.ReplaceItemId);
        if (replaced is null)
        {
            return NotInItinerary();
        }

        var replaceIndex = visits.ToList().IndexOf(replaced);
        var anchor = replaceIndex > 0 ? visits[replaceIndex - 1] : replaced;
        return Result.Success((anchor, -1, replaceIndex, replaced.PointOfInterest?.Category?.Name));
    }

    private static Result<(ItineraryItem, int, int, string?)> NotInItinerary() =>
        Result.Failure<(ItineraryItem, int, int, string?)>(
            ItineraryErrorCodes.InvalidState,
            "The selected stop is not part of the current itinerary.");

    private async Task<List<PointOfInterest>> LoadPlacesAsync(
        SchedulingRequest request,
        string? category,
        IReadOnlySet<long> currentIds,
        CancellationToken cancellationToken)
    {
        var bounds = LocationBounds.From(
            request.ExplorationLatitude,
            request.ExplorationLongitude,
            (int)Math.Ceiling(request.SearchRadiusKm));
        var isFood = PoiCategoryNames.IsFoodAndDrink(category);
        var foodAliases = PoiCategoryNames.FoodAndDrinkAliases
            .Select(alias => alias.ToLowerInvariant()).ToArray();
        var baseQuery = dbContext.PointsOfInterest
            .AsNoTracking()
            .Include(poi => poi.Category)
            .Include(poi => poi.OpeningHours)
            .Where(poi => poi.Status == PointOfInterestStatus.Active
                && poi.Latitude >= bounds.MinimumLatitude
                && poi.Latitude <= bounds.MaximumLatitude
                && poi.Longitude >= bounds.MinimumLongitude
                && poi.Longitude <= bounds.MaximumLongitude);

        List<PointOfInterest> FilterReady(IEnumerable<PointOfInterest> list) =>
            list.Where(poi => !currentIds.Contains(poi.Id))
                .Where(PlanningPoiEligibility.IsPlanningReady)
                .Where(poi => GeoDistance.EquirectangularKilometers(
                    request.ExplorationLatitude,
                    request.ExplorationLongitude,
                    poi.Latitude,
                    poi.Longitude) <= request.SearchRadiusKm)
                .ToList();

        if (isFood)
        {
            var foodPlaces = await baseQuery
                .Where(poi => foodAliases.Contains(poi.Category.Name.Trim().ToLower()))
                .ToListAsync(cancellationToken);
            return FilterReady(foodPlaces);
        }

        if (!string.IsNullOrEmpty(category))
        {
            var sameCategoryPlaces = await baseQuery
                .Where(poi => poi.Category.Name == category)
                .ToListAsync(cancellationToken);
            var eligibleSame = FilterReady(sameCategoryPlaces);
            if (eligibleSame.Count > 0)
            {
                return eligibleSame;
            }
        }

        var anyAttractions = await baseQuery
            .Where(poi => !foodAliases.Contains(poi.Category.Name.Trim().ToLower()))
            .ToListAsync(cancellationToken);
        return FilterReady(anyAttractions);
    }

    private static ItineraryStop[] WithCandidate(
        IReadOnlyList<ItineraryStop> stops,
        long candidateId,
        int insertIndex,
        int replaceIndex)
    {
        var list = stops.ToList();
        if (replaceIndex >= 0)
        {
            list[replaceIndex] = new ItineraryStop(candidateId, list[replaceIndex].TransportModeToNext);
        }
        else
        {
            list.Insert(Math.Clamp(insertIndex, 0, list.Count), new ItineraryStop(candidateId));
        }

        return [.. list];
    }

    private static ItinerarySuggestionDto ToSuggestion(
        PointOfInterest poi,
        decimal straightLineKm,
        string? thumbnailUrl,
        StopsEvaluation? evaluation,
        DateTimeOffset? baselineEnd,
        SchedulingRequest request)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        var plan = evaluation?.Plan;
        var planItems = plan?.Items.OrderBy(item => item.SequenceNo).ToArray() ?? [];
        var index = Array.FindIndex(planItems, item => item.PointOfInterestId == poi.Id);
        var roadMeters = index > 0 ? planItems[index - 1].TravelDistanceToNextMeters : null;
        var infeasibility = evaluation?.Infeasibility;
        // The diagnosis names the FIRST stop that breaks; it may be a later stop this suggestion pushes back.
        var breaksAtThisPlace = infeasibility?.PoiId == poi.Id;
        var arrival = index >= 0
            ? planItems[index].PlannedArrivalUtc
            : breaksAtThisPlace ? infeasibility!.PlannedArrival : null;
        bool? fits = evaluation is null ? null : evaluation.Fits;
        bool? openAtArrival = evaluation is null || (fits == false && infeasibility is null)
            ? null
            : !(breaksAtThisPlace && infeasibility!.Reason == StopInfeasibilityReasons.ClosedAtArrival);
        return new ItinerarySuggestionDto(
            poi.Id,
            poi.Name,
            poi.Category?.Name,
            thumbnailUrl,
            poi.Address,
            CostKindPolicy.FromCategory(poi.Category?.Name),
            poi.EstimatedVisitCost,
            roadMeters ?? (int)Math.Round(straightLineKm * 1000m),
            plan is not null && baselineEnd is not null
                ? (int)Math.Round((plan.EndAtUtc - baselineEnd.Value).TotalMinutes)
                : null,
            roadMeters is not null ? SuggestionDistanceSources.Road : SuggestionDistanceSources.StraightLine,
            arrival,
            openAtArrival,
            ClosesAt(poi, arrival, timeZone),
            fits,
            infeasibility?.Reason,
            infeasibility?.PoiId,
            infeasibility?.OverflowMinutes,
            infeasibility?.OverflowVnd,
            null);
    }

    private static string? ClosesAt(PointOfInterest poi, DateTimeOffset? arrivalUtc, TimeZoneInfo timeZone)
    {
        var day = arrivalUtc is { } arrival
            ? TimeZoneInfo.ConvertTime(arrival, timeZone).DayOfWeek
            : (DayOfWeek?)null;
        var closesAt = poi.OpeningHours
            .Where(hour => !hour.IsClosed && hour.CloseTime.HasValue && (day is null || hour.DayOfWeek == (byte)day))
            .Select(hour => hour.CloseTime)
            .FirstOrDefault();
        return closesAt?.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}