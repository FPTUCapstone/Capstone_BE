using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Reject;

/// <summary>
/// Keep Current Route (UC-15, spec section 7). It closes the proposal as Rejected under the same trip lock as Accept,
/// so the two can never both win. The itinerary and the session are never touched (PC-02).
/// </summary>
public sealed class RejectReroutingProposalCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    IOptions<ReroutingOptions> options,
    IItineraryMutationLock mutationLock)
    : IRequestHandler<RejectReroutingProposalCommand, Result>
{
    private const int MaxAttempts = 3;

    private readonly ReroutingOptions options = options.Value;

    public async Task<Result> Handle(RejectReroutingProposalCommand request, CancellationToken cancellationToken)
    {
        var requestedItineraryId = await dbContext.TripSessions.AsNoTracking()
            .Where(session => session.Id == request.SessionId && session.TravelerUserId == request.TravelerUserId)
            .Select(session => (long?)session.RequestedItineraryId)
            .SingleOrDefaultAsync(cancellationToken);
        var lockResource = requestedItineraryId.HasValue
            ? await ItineraryMutationLockResource.ResolveAsync(dbContext, requestedItineraryId.Value, cancellationToken)
            : null;
        if (lockResource is null || lockResource.IsFailure)
        {
            return NotFound();
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
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<Result> AttemptAsync(RejectReroutingProposalCommand request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var proposal = await dbContext.ReroutingEvents.SingleOrDefaultAsync(
            candidate => candidate.Id == request.ProposalId && candidate.SessionId == request.SessionId,
            cancellationToken);
        var session = await dbContext.TripSessions.SingleOrDefaultAsync(
            candidate => candidate.Id == request.SessionId, cancellationToken);
        if (proposal is null
            || session is null
            || session.TravelerUserId != request.TravelerUserId
            || ReroutingProposalSnapshot.TryParse(proposal.ProposedItinerarySnapshot) is null)
        {
            return NotFound();
        }

        switch (ReroutingDecisionGate.Evaluate(proposal, session, ReroutingEvent.StatusRejected, now, options.ProposalTtl))
        {
            case ReroutingGate.Replay:
                return Result.Success();
            case ReroutingGate.Closed:
                return Result.Failure(ReroutingErrorCodes.ProposalClosed, ReroutingDecisionGate.Message);
            case ReroutingGate.Expired:
                await dbContext.SaveChangesAsync(cancellationToken);
                return Result.Failure(ReroutingErrorCodes.ProposalExpired, ReroutingDecisionGate.Message);
        }

        proposal.Reject(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static Result NotFound() => Result.Failure(ReroutingErrorCodes.NotFound, ReroutingDecisionGate.Message);
}