using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Itineraries.List;

public sealed record ListMyItinerariesQuery(
    long TravelerUserId,
    int Page,
    int PageSize,
    string? Sort = null)
    : IRequest<Result<ItinerarySummaryPage>>
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const string PrioritySort = "priority";
    public const string NewestSort = "newest";
}