using System.Linq.Expressions;

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
            eligiblePois = eligiblePois.Where(poi =>
                poi.Latitude >= bounds.MinimumLatitude
                && poi.Latitude <= bounds.MaximumLatitude
                && poi.Longitude >= bounds.MinimumLongitude
                && poi.Longitude <= bounds.MaximumLongitude);
        }

        var matches = eligiblePois.Select(CreateMatchProjection(
            hasLocation,
            request.Latitude,
            request.Longitude));

        if (hasLocation)
        {
            matches = matches.Where(match => match.DistanceKm <= request.RadiusKm!.Value);
        }

        var totalCount = await matches.CountAsync(cancellationToken);
        var offset = checked((long)(request.Page - 1) * request.PageSize);
        var orderedMatches = hasLocation
            ? matches.OrderBy(match => match.DistanceKm).ThenBy(match => match.Name)
            : matches.OrderBy(match => match.Name);
        var pageMatches = offset > int.MaxValue
            ? Array.Empty<SearchMatch>()
            : await orderedMatches
                .Skip((int)offset)
                .Take(request.PageSize)
                .ToArrayAsync(cancellationToken);

        var items = pageMatches.Select(match => new SelectablePoiDto(
                match.Id,
                match.Name,
                match.CategoryName,
                match.Address,
                match.Latitude,
                match.Longitude,
                match.AverageVisitDurationMinutes,
                match.EstimatedVisitCost,
                true,
                match.HasShelter))
            .ToArray();

        return Result.Success(new SelectablePoiSearchResult(
            items,
            request.Page,
            request.PageSize,
            totalCount));
    }

    private static Expression<Func<PointOfInterest, SearchMatch>> CreateMatchProjection(
        bool hasLocation,
        decimal? originLatitude,
        decimal? originLongitude)
    {
        var latitudeRadians = (double)(originLatitude ?? 0m) * Math.PI / 180d;
        var longitudeRadians = (double)(originLongitude ?? 0m) * Math.PI / 180d;
        return poi => new SearchMatch(
            poi.Id,
            poi.Name,
            poi.Category.Name,
            poi.Address,
            poi.Latitude,
            poi.Longitude,
            poi.AverageVisitDurationMinutes,
            poi.EstimatedVisitCost,
            poi.HasShelter,
            hasLocation
                ? 12_742d * Math.Atan2(
                    Math.Sqrt(
                        Math.Sin((((double)poi.Latitude * Math.PI / 180d) - latitudeRadians) / 2d)
                        * Math.Sin((((double)poi.Latitude * Math.PI / 180d) - latitudeRadians) / 2d)
                        + Math.Cos(latitudeRadians)
                        * Math.Cos((double)poi.Latitude * Math.PI / 180d)
                        * Math.Sin((((double)poi.Longitude * Math.PI / 180d) - longitudeRadians) / 2d)
                        * Math.Sin((((double)poi.Longitude * Math.PI / 180d) - longitudeRadians) / 2d)),
                    Math.Sqrt(1d - (
                        Math.Sin((((double)poi.Latitude * Math.PI / 180d) - latitudeRadians) / 2d)
                        * Math.Sin((((double)poi.Latitude * Math.PI / 180d) - latitudeRadians) / 2d)
                        + Math.Cos(latitudeRadians)
                        * Math.Cos((double)poi.Latitude * Math.PI / 180d)
                        * Math.Sin((((double)poi.Longitude * Math.PI / 180d) - longitudeRadians) / 2d)
                        * Math.Sin((((double)poi.Longitude * Math.PI / 180d) - longitudeRadians) / 2d))))
                : 0d);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private sealed record SearchMatch(
        long Id,
        string Name,
        string CategoryName,
        string? Address,
        decimal Latitude,
        decimal Longitude,
        int AverageVisitDurationMinutes,
        decimal? EstimatedVisitCost,
        bool HasShelter,
        double DistanceKm);

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