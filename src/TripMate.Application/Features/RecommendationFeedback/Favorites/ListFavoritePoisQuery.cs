using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.RecommendationFeedback.Favorites;

public sealed record ListFavoritePoisQuery(
    long TravelerUserId,
    int Page = ListFavoritePoisQuery.DefaultPage,
    int PageSize = ListFavoritePoisQuery.DefaultPageSize) : IRequest<Result<PagedFavoritePoisResponse>>
{
    public const int MinPage = 1;
    public const int DefaultPage = 1;
    public const int MinPageSize = 1;
    public const int DefaultPageSize = 12;
    public const int MaxPageSize = 50;
}