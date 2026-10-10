using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.List;

/// <summary>
/// Lists the current version of every itinerary the Traveler can open with the detail query: the
/// ones they own and the ones shared through an active group membership.
/// </summary>
public sealed class ListMyItinerariesQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock)
    : IRequestHandler<ListMyItinerariesQuery, Result<ItinerarySummaryPage>>
{
    public async Task<Result<ItinerarySummaryPage>> Handle(
        ListMyItinerariesQuery request,
        CancellationToken cancellationToken)
    {
        var travelerUserId = request.TravelerUserId;
        var ownedRequestIds = dbContext.Itineraries
            .Where(itinerary => itinerary.TravelerUserId == travelerUserId
                && itinerary.SchedulingRequestId != null)
            .Select(itinerary => itinerary.SchedulingRequestId);
        var groupRequestIds = dbContext.TravelGroups
            .Where(group => group.Itinerary.SchedulingRequestId != null
                && group.GroupMembers.Any(member =>
                    member.UserId == travelerUserId
                    && member.Status == GroupMemberStatus.Active))
            .Select(group => group.Itinerary.SchedulingRequestId);

        var accessible = dbContext.Itineraries
            .AsNoTracking()
            .Where(itinerary =>
                (itinerary.SchedulingRequestId == null && itinerary.TravelerUserId == travelerUserId)
                || (itinerary.SchedulingRequestId != null
                    && (ownedRequestIds.Contains(itinerary.SchedulingRequestId)
                        || groupRequestIds.Contains(itinerary.SchedulingRequestId))
                    && !dbContext.Itineraries.Any(newer =>
                        newer.SchedulingRequestId == itinerary.SchedulingRequestId
                        && (newer.Version > itinerary.Version
                            || (newer.Version == itinerary.Version && newer.Id > itinerary.Id)))));

        var totalCount = await accessible.CountAsync(cancellationToken);
        var offset = (long)(request.Page - 1) * request.PageSize;
        var summaries = offset >= totalCount
            ? []
            : await accessible
                .OrderBy(itinerary => itinerary.ValidFromUtc == null)
                .ThenByDescending(itinerary => itinerary.ValidFromUtc)
                .ThenByDescending(itinerary => itinerary.Id)
                .Skip((int)offset)
                .Take(request.PageSize)
                .Select(itinerary => new ItinerarySummaryResponse(
                    itinerary.Id,
                    itinerary.SchedulingRequestId,
                    itinerary.Title,
                    itinerary.Version,
                    itinerary.Status,
                    itinerary.ValidFromUtc,
                    itinerary.ValidToUtc,
                    itinerary.TravelerUserId == travelerUserId,
                    itinerary.Items.Count(item => item.Kind == ItineraryItemKind.Visit),
                    null))
                .ToListAsync(cancellationToken);

        var openSession = await FindOpenSessionAsync(travelerUserId, cancellationToken);
        var items = openSession is null
            ? summaries
            : summaries
                .Select(summary => IsSameTrip(summary, openSession)
                    ? summary with { OpenNavigationSessionId = openSession.SessionId }
                    : summary)
                .ToList();

        return Result.Success(new ItinerarySummaryPage(
            items,
            request.Page,
            request.PageSize,
            totalCount));
    }

    private async Task<OpenSession?> FindOpenSessionAsync(
        long travelerUserId,
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;
        return await dbContext.TripSessions
            .AsNoTracking()
            .Where(session => session.TravelerUserId == travelerUserId
                && session.EndedAtUtc == null
                && (session.FsmState == TripSession.NavigatingState
                    || session.FsmState == TripSession.ExploringState)
                && (session.ExpiresAtUtc == null || session.ExpiresAtUtc > nowUtc))
            .Select(session => new OpenSession(
                session.Id,
                session.ItineraryId,
                session.Itinerary.SchedulingRequestId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    // A session freezes the version it started on; a later version of the same scheduling
    // request is still the same trip.
    private static bool IsSameTrip(ItinerarySummaryResponse summary, OpenSession session) =>
        summary.ItineraryId == session.ItineraryId
        || (summary.SchedulingRequestId is { } requestId && requestId == session.SchedulingRequestId);

    private sealed record OpenSession(long SessionId, long ItineraryId, long? SchedulingRequestId);
}