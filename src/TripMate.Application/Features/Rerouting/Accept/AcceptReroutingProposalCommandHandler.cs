using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.SystemConfigs.Common;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Engine;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Accept;

/// <summary>
/// Accept Re-routing (UC-15, spec section 6). One serializable transaction under the trip's mutation lock:
/// the TTL and the base stops are re-checked at the moment of the call, the proposed order is evaluated again from
/// now, and only then is a new itinerary version created and the session moved onto it. Double taps replay.
/// </summary>
public sealed class AcceptReroutingProposalCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    IOptions<ReroutingOptions> options,
    IItineraryVersionService versionService,
    IItineraryAccessService accessService,
    IItineraryMutationLock mutationLock,
    ILogger<AcceptReroutingProposalCommandHandler>? logger = null)
    : IRequestHandler<AcceptReroutingProposalCommand, Result<NavigationSessionResponse>>
{
    private const int MaxAttempts = 3;

    private readonly ReroutingOptions options = options.Value;

    public async Task<Result<NavigationSessionResponse>> Handle(
        AcceptReroutingProposalCommand request,
        CancellationToken cancellationToken)
    {
        // Nothing is disclosed about a session or proposal that is not the caller's, and no lock is taken for it.
        var requestedItineraryId = await dbContext.TripSessions.AsNoTracking()
            .Where(session => session.Id == request.SessionId && session.TravelerUserId == request.TravelerUserId)
            .Select(session => (long?)session.RequestedItineraryId)
            .SingleOrDefaultAsync(cancellationToken);
        var lockResource = requestedItineraryId.HasValue
            ? await ItineraryMutationLockResource.ResolveAsync(dbContext, requestedItineraryId.Value, cancellationToken)
            : null;
        if (lockResource is null || lockResource.IsFailure)
        {
            return ReroutingDecisionGate.NotFound<NavigationSessionResponse>();
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(
                    async transactionCancellationToken =>
                    {
                        await mutationLock.AcquireAsync(lockResource.Value, transactionCancellationToken);
                        return await AttemptAsync(request, transactionCancellationToken);
                    },
                    cancellationToken);
            }
            catch (Exception exception) when (attempt < MaxAttempts
                && (exception is DbUpdateConcurrencyException || dbContext.IsDeadlock(exception)))
            {
                // A concurrent reach/skip or a deadlock rolled the attempt back: run it again from fresh state.
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<Result<NavigationSessionResponse>> AttemptAsync(
        AcceptReroutingProposalCommand request,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var proposal = await dbContext.ReroutingEvents.SingleOrDefaultAsync(
            candidate => candidate.Id == request.ProposalId && candidate.SessionId == request.SessionId,
            cancellationToken);
        var session = await dbContext.TripSessions
            .AsSplitQuery()
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.StateHistory)
            .Include(candidate => candidate.Itinerary)
                .ThenInclude(itinerary => itinerary.SchedulingRequest)
            .Include(candidate => candidate.Itinerary)
                .ThenInclude(itinerary => itinerary.Items)
            .SingleOrDefaultAsync(candidate => candidate.Id == request.SessionId, cancellationToken);
        var snapshot = proposal is null ? null : ReroutingProposalSnapshot.TryParse(proposal.ProposedItinerarySnapshot);
        if (proposal is null || session is null || snapshot is null || session.TravelerUserId != request.TravelerUserId)
        {
            return ReroutingDecisionGate.NotFound<NavigationSessionResponse>();
        }

        // The Traveler must still have access to the trip (a group membership may have been revoked meanwhile).
        var access = await dbContext.ExecuteWithSerializableReadsAsync(
            readCancellationToken => accessService.ResolveCurrentAsync(
                session.RequestedItineraryId, request.TravelerUserId, trackCurrent: false, readCancellationToken),
            cancellationToken);
        if (access.IsFailure)
        {
            return ReroutingDecisionGate.NotFound<NavigationSessionResponse>();
        }

        switch (ReroutingDecisionGate.Evaluate(proposal, session, ReroutingEvent.StatusAccepted, now, options.ProposalTtl))
        {
            case ReroutingGate.Replay:
                return Result.Success(await ResponseAsync(session, session.Itinerary, cancellationToken));
            case ReroutingGate.Closed:
                return ReroutingDecisionGate.Closed<NavigationSessionResponse>();
            case ReroutingGate.Expired:
                await dbContext.SaveChangesAsync(cancellationToken);
                return ReroutingDecisionGate.Expired<NavigationSessionResponse>();
        }

        // The Traveler moved on (reached or skipped a stop the proposal was built on) or the trip is on another version.
        var pendingIds = session.Items
            .Where(item => item.Status == TripSessionItem.PendingStatus)
            .Select(item => item.ItineraryItemId)
            .ToHashSet();
        if (session.ItineraryId != snapshot.BaseItineraryId
            || snapshot.BaseStops.Any(stop => !pendingIds.Contains(stop.ItineraryItemId)))
        {
            return await ExpireAsync(proposal, now, ReroutingDecisionGate.Expired<NavigationSessionResponse>(), cancellationToken);
        }

        var schedulingRequest = session.Itinerary.SchedulingRequest;
        if (schedulingRequest is null)
        {
            return await ExpireAsync(proposal, now, ReroutingDecisionGate.Infeasible<NavigationSessionResponse>(), cancellationToken);
        }

        // BR-25 at the moment of application: evaluate the proposed order again from now. The fresh plan is the one saved.
        var evaluation = await EvaluateAsync(session, schedulingRequest, snapshot, now, cancellationToken);
        if (evaluation.IsFailure || evaluation.Value[0].Plan is null)
        {
            return await ExpireAsync(proposal, now, ReroutingDecisionGate.Infeasible<NavigationSessionResponse>(), cancellationToken);
        }

        var applied = await new RerouteApplier(dbContext, versionService).ApplyAsync(
            session, proposal, schedulingRequest, evaluation.Value[0].Plan!, now, cancellationToken);
        logger?.LogInformation(
            "UC-15 proposal {ProposalId} accepted: session {SessionId} moved to itinerary {ItineraryId} version {Version}.",
            proposal.Id,
            session.Id,
            applied.Id,
            applied.Version);
        return Result.Success(await ResponseAsync(session, applied, cancellationToken));
    }

    private async Task<Result<IReadOnlyList<StopsEvaluation>>> EvaluateAsync(
        TripSession session,
        SchedulingRequest schedulingRequest,
        ReroutingProposalSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var affected = session.Items.FirstOrDefault(item => snapshot.AffectedItineraryItemIds.Contains(item.ItineraryItemId))
            ?? session.Items.First(item => item.ItineraryItemId == snapshot.BaseStops[0].ItineraryItemId);
        var itemIds = session.Items.Select(item => item.ItineraryItemId).ToArray();
        var costs = await dbContext.ItineraryItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.EstimatedCost, cancellationToken);
        var radius = await dbContext.SystemConfigs.AsNoTracking()
            .Where(config => config.ConfigKey == AlgorithmParameterDefinitions.ReroutingSearchRadiusKmKey)
            .Select(config => config.ConfigValue)
            .SingleOrDefaultAsync(cancellationToken);
        var anchor = ReroutingAnchorFactory.Create(
            session,
            schedulingRequest,
            affected,
            costs,
            (decimal)AlgorithmParameterDefinitions.ParseSearchRadius(radius),
            now);
        return await versionService.EvaluateStopVariantsAsync(
            schedulingRequest,
            [snapshot.ProposedStops.Select(stop => new ItineraryStop(stop.PoiId)).ToArray()],
            cancellationToken,
            anchor: anchor);
    }

    /// <summary>The proposal is closed as Expired (committed), nothing else changes.</summary>
    private async Task<Result<NavigationSessionResponse>> ExpireAsync(
        ReroutingEvent proposal,
        DateTimeOffset now,
        Result<NavigationSessionResponse> failure,
        CancellationToken cancellationToken)
    {
        proposal.Expire(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return failure;
    }

    private async Task<NavigationSessionResponse> ResponseAsync(
        TripSession session,
        Itinerary itinerary,
        CancellationToken cancellationToken) =>
        await ActiveRouteNavigationProjection.EnrichAsync(
            dbContext, NavigationSessionResponse.From(session, itinerary), cancellationToken);
}