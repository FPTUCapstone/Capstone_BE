using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Search;

public sealed class SearchSelectablePoisQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<SearchSelectablePoisQuery, Result<SelectablePoiSearchResult>>
{
    public async Task<Result<SelectablePoiSearchResult>> Handle(
        SearchSelectablePoisQuery request,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = request.Query?.Trim();
        var hasLocation = request.Latitude.HasValue;
        var pois = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Include(poi => poi.Category)
            .Include(poi => poi.OpeningHours)
            .Where(poi =>
                poi.Status == PointOfInterestStatus.Active
                && poi.OpeningHours.Any()
                && poi.SourceUrl != null
                && poi.VerifiedAtUtc != null)
            .ToListAsync(cancellationToken);

        var matches = pois
            .Where(poi => string.IsNullOrEmpty(normalizedQuery)
                || poi.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Select(poi => new
            {
                Poi = poi,
                DistanceKm = hasLocation
                    ? HaversineDistanceKm(
                        request.Latitude!.Value,
                        request.Longitude!.Value,
                        poi.Latitude,
                        poi.Longitude)
                    : (decimal?)null,
            })
            .Where(match => !hasLocation || match.DistanceKm <= request.RadiusKm)
            .OrderBy(match => match.DistanceKm ?? decimal.MaxValue)
            .ThenBy(match => match.Poi.Name)
            .ToArray();

        var items = matches
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(match => new SelectablePoiDto(
                match.Poi.Id,
                match.Poi.Name,
                match.Poi.Category.Name,
                match.Poi.Address,
                match.Poi.Latitude,
                match.Poi.Longitude,
                match.Poi.AverageVisitDurationMinutes,
                match.Poi.EstimatedVisitCost,
                true,
                match.Poi.HasShelter))
            .ToArray();

        return Result.Success(new SelectablePoiSearchResult(
            items,
            request.Page,
            request.PageSize,
            matches.Length));
    }

    private static decimal HaversineDistanceKm(
        decimal originLatitude,
        decimal originLongitude,
        decimal destinationLatitude,
        decimal destinationLongitude)
    {
        const double earthRadiusKm = 6371d;
        var latitudeDelta = DegreesToRadians((double)(destinationLatitude - originLatitude));
        var longitudeDelta = DegreesToRadians((double)(destinationLongitude - originLongitude));
        var originLatitudeRadians = DegreesToRadians((double)originLatitude);
        var destinationLatitudeRadians = DegreesToRadians((double)destinationLatitude);
        var haversine = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2)
            + Math.Cos(originLatitudeRadians) * Math.Cos(destinationLatitudeRadians)
            * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        var centralAngle = 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1 - haversine));
        return (decimal)(earthRadiusKm * centralAngle);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
