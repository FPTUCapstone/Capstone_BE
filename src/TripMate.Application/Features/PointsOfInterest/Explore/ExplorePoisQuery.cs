using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.PointsOfInterest.Explore;

public sealed record ExplorePoisQuery : IRequest<Result<PagedPoiResponseDto>>
{
    public const int SearchMaxLength = 200;
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const string SortName = "name";
    public const string SortDistance = "distance";
    public const string SortRating = "rating";
    public const string DefaultSort = SortName;
    public static readonly string[] AllowedSorts = [SortName, SortDistance, SortRating];

    public const decimal MinLatitude = -90m;
    public const decimal MaxLatitude = 90m;
    public const decimal MinLongitude = -180m;
    public const decimal MaxLongitude = 180m;
    public const decimal MinDistanceKm = 0m;
    public const int MinCategoryId = 1;
    public const int MinPage = 1;
    public const int MinPageSize = 1;

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