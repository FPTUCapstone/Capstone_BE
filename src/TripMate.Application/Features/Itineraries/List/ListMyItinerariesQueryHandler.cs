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
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(
                    token => ReadAsync(request, token), cancellationToken);
            }
            catch (Exception exception) when (attempt < 3 && dbContext.IsDeadlock(exception))
            {
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<Result<ItinerarySummaryPage>> ReadAsync(
        ListMyItinerariesQuery request,
        CancellationToken cancellationToken)
    {
        var travelerUserId = request.TravelerUserId;
        var ownedRequestIds = dbContext.Itineraries
            .Where(itinerary => itinerary.TravelerUserId == travelerUserId
                && itinerary.SchedulingRequestId != null)
            .Select(itinerary => itinerary.SchedulingRequestId);
        var groupRequestIds = dbContext.TravelGroups
            .Where(group => group.Status == TravelGroupStatus.Active
                && group.Itinerary.SchedulingRequestId != null
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
        var openSession = await FindOpenSessionAsync(travelerUserId, cancellationToken);
        var openItineraryId = openSession?.ItineraryId;
        var openRequestId = openSession?.SchedulingRequestId;
        var nowUtc = clock.UtcNow;

        // The trip in progress comes first, then Active trips that have not ended yet (soonest
        // first), then drafts with the one just planned or regenerated on top (a new version gets
        // a new id), then everything else from the newest start. Pages stay stable because the
        // order ends on the unique id.
        var summaries = offset >= totalCount
            ? []
            : await accessible
                .OrderByDescending(itinerary => itinerary.Id == openItineraryId
                    || (openRequestId != null && itinerary.SchedulingRequestId == openRequestId))
                .ThenByDescending(itinerary => itinerary.Status == Itinerary.ActiveStatus
                    && itinerary.ValidToUtc != null
                    && itinerary.ValidToUtc >= nowUtc)
                .ThenBy(itinerary => itinerary.Status == Itinerary.ActiveStatus
                    && itinerary.ValidToUtc != null
                    && itinerary.ValidToUtc >= nowUtc
                        ? itinerary.ValidFromUtc
                        : null)
                .ThenByDescending(itinerary => itinerary.Status == Itinerary.DraftStatus)
                .ThenByDescending(itinerary => itinerary.Status == Itinerary.DraftStatus
                    ? itinerary.Id
                    : (long?)null)
                .ThenBy(itinerary => itinerary.ValidFromUtc == null)
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
