using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.PointsOfInterest.Search;

public sealed record SearchSelectablePoisQuery(
    string? Query,
    decimal? Latitude,
    decimal? Longitude,
    int? RadiusKm,
    int Page,
    int PageSize,
    bool ExcludeFoodAndDrink = false,
    bool ExcludeAccommodation = false)
    : IRequest<Result<SelectablePoiSearchResult>>;