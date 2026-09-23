using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
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
        DistanceCalculation? distance = null;
        IQueryable<PointOfInterest> eligiblePois = dbContext.PointsOfInterest
            .AsNoTracking()
            .Where(poi =>
                poi.Status == PointOfInterestStatus.Active
                && poi.OpeningHours.Any()
                && poi.SourceUrl != null
                && poi.VerifiedAtUtc != null);

        if (!string.IsNullOrEmpty(normalizedQuery))
        {
            eligiblePois = eligiblePois.Where(poi => poi.Name.Contains(normalizedQuery));
        }

        if (hasLocation)
        {
            var bounds = LocationBounds.From(request.Latitude!.Value, request.Longitude!.Value, request.RadiusKm!.Value);
            distance = DistanceCalculation.Create(
                request.Latitude.Value,
                request.Longitude!.Value,
                bounds);
            eligiblePois = eligiblePois.Where(poi =>
                poi.Latitude >= bounds.MinimumLatitude
                && poi.Latitude <= bounds.MaximumLatitude
                && poi.Longitude >= bounds.MinimumLongitude
                && poi.Longitude <= bounds.MaximumLongitude
                && ((poi.Latitude - distance.Latitude) * (poi.Latitude - distance.Latitude)
                    * distance.LatitudeWeight)
                   + ((poi.Longitude - distance.Longitude) * (poi.Longitude - distance.Longitude)
                    * distance.LongitudeWeight)
                   <= distance.RadiusMetric);
        }

        var totalCount = await eligiblePois.CountAsync(cancellationToken);
        var offset = checked((long)(request.Page - 1) * request.PageSize);
        IOrderedQueryable<PointOfInterest> orderedPois;
        if (distance is not null)
        {
            orderedPois = eligiblePois
                .OrderBy(poi =>
                    ((poi.Latitude - distance.Latitude) * (poi.Latitude - distance.Latitude)
                     * distance.LatitudeWeight)
                    + ((poi.Longitude - distance.Longitude) * (poi.Longitude - distance.Longitude)
                     * distance.LongitudeWeight))
                .ThenBy(poi => poi.Name);
        }
        else
        {
            orderedPois = eligiblePois.OrderBy(poi => poi.Name);
        }
        var items = offset > int.MaxValue
            ? Array.Empty<SelectablePoiDto>()
            : await orderedPois
                .Skip((int)offset)
                .Take(request.PageSize)
                .Select(poi => new SelectablePoiDto(
                    poi.Id,
                    poi.Name,
                    poi.Category.Name,
                    poi.Address,
                    poi.Latitude,
                    poi.Longitude,
                    poi.AverageVisitDurationMinutes,
                    poi.EstimatedVisitCost,
                    true,
                    poi.HasShelter))
                .ToArrayAsync(cancellationToken);

        return Result.Success(new SelectablePoiSearchResult(
            items,
            request.Page,
            request.PageSize,
            totalCount));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private sealed record DistanceCalculation(
        decimal Latitude,
        decimal Longitude,
        decimal LatitudeWeight,
        decimal LongitudeWeight,
        decimal RadiusMetric)
    {
        public static DistanceCalculation Create(
            decimal latitude,
            decimal longitude,
            LocationBounds bounds)
        {
            var latitudeRadiusDegrees = bounds.MaximumLatitude - latitude;
            var longitudeRadiusDegrees = bounds.MaximumLongitude - longitude;
            var latitudeRadiusSquared = latitudeRadiusDegrees * latitudeRadiusDegrees;
            var longitudeRadiusSquared = longitudeRadiusDegrees * longitudeRadiusDegrees;
            return new DistanceCalculation(
                latitude,
                longitude,
                longitudeRadiusSquared,
                latitudeRadiusSquared,
                latitudeRadiusSquared * longitudeRadiusSquared);
        }
    }

    private sealed record LocationBounds(
        decimal MinimumLatitude,
        decimal MaximumLatitude,
        decimal MinimumLongitude,
        decimal MaximumLongitude)
    {
        private const decimal LatitudeKilometersPerDegree = 110.574m;
        private const double LongitudeKilometersPerDegreeAtEquator = 111.320d;

        public static LocationBounds From(decimal latitude, decimal longitude, int radiusKm)
        {
            var latitudeDelta = radiusKm / LatitudeKilometersPerDegree;
            var cosine = Math.Abs(Math.Cos(DegreesToRadians((double)latitude)));
            var longitudeDelta = cosine < 0.000001d
                ? 180m
                : Math.Min(
                    180m,
                    (decimal)(radiusKm / (LongitudeKilometersPerDegreeAtEquator * cosine)));
            return new LocationBounds(
                Math.Max(-90m, latitude - latitudeDelta),
                Math.Min(90m, latitude + latitudeDelta),
                Math.Max(-180m, longitude - longitudeDelta),
                Math.Min(180m, longitude + longitudeDelta));
        }
    }
}