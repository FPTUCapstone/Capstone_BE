using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.PointsOfInterest.Explore;

public sealed record ExplorePoisQuery : IRequest<Result<PagedPoiResponseDto>>
{
    public const int SearchMaxLength = 200;
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const string DefaultSort = "name";

    public string? Search { get; init; }

    public int? CategoryId { get; init; }

    public decimal? OriginLatitude { get; init; }

    public decimal? OriginLongitude { get; init; }

    public decimal? MaxDistanceKm { get; init; }

    public bool OpenNow { get; init; } = false;

    public string Sort { get; init; } = DefaultSort;

    public int Page { get; init; } = DefaultPage;

    public int PageSize { get; init; } = DefaultPageSize;
}