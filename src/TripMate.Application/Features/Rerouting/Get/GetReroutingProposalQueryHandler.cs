using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Get;

public sealed class GetReroutingProposalQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    IOptions<ReroutingOptions> options)
    : IRequestHandler<GetReroutingProposalQuery, Result<ReroutingProposalDetailResponse>>
{
    private readonly ReroutingOptions options = options.Value;

    public async Task<Result<ReroutingProposalDetailResponse>> Handle(
        GetReroutingProposalQuery request,
        CancellationToken cancellationToken)
    {
        // Another Traveler's session, a proposal of another session, an unknown id and a row without a valid
        // snapshot all read as "not found": nothing is disclosed (spec section 5).
        var ownsSession = await dbContext.TripSessions.AsNoTracking().AnyAsync(
            session => session.Id == request.SessionId && session.TravelerUserId == request.TravelerUserId,
            cancellationToken);
        var proposal = ownsSession
            ? await dbContext.ReroutingEvents.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == request.ProposalId && candidate.SessionId == request.SessionId,
                cancellationToken)
            : null;
        var snapshot = proposal is null ? null : ReroutingProposalSnapshot.TryParse(proposal.ProposedItinerarySnapshot);
        if (proposal is null || snapshot is null)
        {
            return Result.Failure<ReroutingProposalDetailResponse>(
                ReroutingErrorCodes.NotFound,
                "No re-routing suggestion is available at the moment.");
        }

        var expired = proposal.Status == ReroutingEvent.StatusProposed
            && proposal.IsExpiredAt(clock.UtcNow, options.ProposalTtl);
        var affected = snapshot.BaseStops
            .Where(stop => snapshot.AffectedItineraryItemIds.Contains(stop.ItineraryItemId))
            .ToArray();
        return Result.Success(new ReroutingProposalDetailResponse(
            proposal.Id,
            proposal.IncidentId,
            expired ? ReroutingEvent.StatusExpired : proposal.Status,
            new ReroutingReasonResponse(
                snapshot.ReasonCode,
                snapshot.Strategy,
                affected.Select(stop => (long?)stop.ItineraryItemId).FirstOrDefault(),
                affected.Select(stop => stop.Name).FirstOrDefault()),
            snapshot.BaseStops.Select(stop => new ReroutingCurrentStopResponse(
                stop.ItineraryItemId, stop.PoiId, stop.Name, stop.Latitude, stop.Longitude,
                stop.PlannedArrivalUtc, stop.PlannedDepartureUtc, stop.IsMandatory)).ToArray(),
            snapshot.ProposedStops.Select(stop => new ReroutingProposedStopResponse(
                stop.PoiId, stop.Name, stop.Latitude, stop.Longitude, stop.PlannedArrivalUtc,
                stop.PlannedDepartureUtc, stop.IsMandatory, stop.TransportModeToNext, stop.Change)).ToArray(),
            affected.Select(stop => new ReroutingAffectedStopResponse(stop.ItineraryItemId, stop.PoiId, stop.Name)).ToArray(),
            snapshot.BaseStops
                .Where(stop => snapshot.RemovedPoiIds.Contains(stop.PoiId))
                .Select(stop => new ReroutingRemovedStopResponse(stop.PoiId, stop.Name)).ToArray(),
            snapshot.TravelTimeDiffMinutes,
            snapshot.ArrivalTimeDiffMinutes,
            proposal.ProposedAtUtc,
            proposal.ProposedAtUtc + options.ProposalTtl,
            proposal.DecidedAtUtc));
    }
}