using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripHistory.Common;

namespace TripMate.Application.Features.TripHistory.List;

/// <summary>
/// UC-32 list: the Traveler's own bookings and self-planned itineraries for one tab. Read-only
/// (PC-03): the handler issues queries only and never saves.
/// </summary>
public sealed class ListTravelerTripsQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock)
    : IRequestHandler<ListTravelerTripsQuery, Result<TripHistoryPage>>
{
    public async Task<Result<TripHistoryPage>> Handle(
        ListTravelerTripsQuery request,
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;
        var keys = await TripHistoryKeyReader.ReadPageAsync(dbContext, request, nowUtc, cancellationToken);
        var items = await TripCardHydrator.HydrateAsync(
            dbContext, request.TravelerUserId, request.Tab!, keys.Keys, nowUtc, cancellationToken);

        TripHistorySummaryResponse? summary = null;
        if (request.Tab == ListTravelerTripsQuery.TabCompleted)
        {
            var visitedPois = await TripHistoryKeyReader.CountVisitedPoisAsync(
                dbContext, request, nowUtc, cancellationToken);
            summary = new TripHistorySummaryResponse(keys.TotalCount, visitedPois);
        }

        return Result.Success(new TripHistoryPage(items, summary, request.Page, request.PageSize, keys.TotalCount));
    }
}