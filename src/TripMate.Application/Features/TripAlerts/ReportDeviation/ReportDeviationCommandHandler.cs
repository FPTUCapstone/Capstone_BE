using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripAlerts.ReportDeviation;

public sealed class ReportDeviationCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    IOptions<TripAlertOptions> options)
    : IRequestHandler<ReportDeviationCommand, Result<TripAlertResponse>>
{
    private readonly TripAlertOptions options = options.Value;
    private readonly TripAlertPersistence persistence = new(dbContext);

    public Task<Result<TripAlertResponse>> Handle(
        ReportDeviationCommand request,
        CancellationToken cancellationToken) =>
        dbContext.ExecuteInTransactionAsync(
            transactionCancellationToken => HandleInTransaction(request, transactionCancellationToken),
            cancellationToken);

    private async Task<Result<TripAlertResponse>> HandleInTransaction(
        ReportDeviationCommand request,
        CancellationToken cancellationToken)
    {
        var session = await dbContext.ExecuteWithSerializableReadsAsync(
            ct => dbContext.TripSessions
                .Include(candidate => candidate.Items)
                .SingleOrDefaultAsync(candidate => candidate.Id == request.SessionId, ct),
            cancellationToken);
        if (session is null)
        {
            return Failure(TripAlertErrorCodes.NotFound, "The navigation session was not found.");
        }

        if (session.TravelerUserId != request.TravelerUserId)
        {
            return Failure(TripAlertErrorCodes.AccessDenied, "You do not have access to this session.");
        }

        var replay = await dbContext.Incidents
            .Include(incident => incident.AffectedItem)
            .SingleOrDefaultAsync(incident =>
                incident.SessionId == request.SessionId
                && incident.ClientEventId == request.EventId,
                cancellationToken);
        if (replay is not null)
        {
            if (replay.AffectedItineraryItemId != request.TargetItemId)
            {
                return Failure(
                    TripAlertErrorCodes.EventPayloadMismatch,
                    "The event ID was already used for a different target.");
            }

            var response = await persistence.FindAsync(replay, request.TravelerUserId, cancellationToken);
            return response is null
                ? Failure(TripAlertErrorCodes.NotFound, "The trip alert was not found.")
                : Result.Success(response);
        }

        if (session.EndedAtUtc.HasValue || session.FsmState == TripSession.CompletedState)
        {
            return Failure(TripAlertErrorCodes.SessionCompleted, "The navigation session has completed.");
        }

        var target = session.Items.SingleOrDefault(item => item.ItineraryItemId == request.TargetItemId);
        if (target is null)
        {
            return Failure(TripAlertErrorCodes.TargetNotNavigable, "The target is not in this session.");
        }

        if (target.Status == TripSessionItem.ReachedStatus)
        {
            return Failure(TripAlertErrorCodes.TargetAlreadyReached, "The target was already reached.");
        }

        var sourceKey = $"deviation:{request.TargetItemId}";
        var open = await dbContext.Incidents
            .Include(incident => incident.AffectedItem)
            .SingleOrDefaultAsync(incident =>
                incident.SessionId == request.SessionId
                && incident.IncidentType == Incident.RouteDeviationType
                && incident.SourceKey == sourceKey
                && incident.ResolvedAtUtc == null,
                cancellationToken);
        if (open is not null)
        {
            var response = await persistence.FindAsync(open, request.TravelerUserId, cancellationToken);
            return response is null
                ? Failure(TripAlertErrorCodes.NotFound, "The trip alert was not found.")
                : Result.Success(response);
        }

        var now = clock.UtcNow;
        var cutoff = now - options.DeviationCooldown;
        var lastDeviation = await dbContext.Incidents.AsNoTracking()
            .Where(incident =>
                incident.SessionId == request.SessionId
                && incident.IncidentType == Incident.RouteDeviationType
                && incident.DetectedAtUtc > cutoff)
            .OrderByDescending(incident => incident.DetectedAtUtc)
            .Select(incident => (DateTimeOffset?)incident.DetectedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (lastDeviation.HasValue)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling(
                (lastDeviation.Value + options.DeviationCooldown - now).TotalSeconds));
            return Result.Failure<TripAlertResponse>(
                TripAlertErrorCodes.DeviationCooldown,
                "A route deviation was reported too recently.",
                new Dictionary<string, object?> { ["retryAfterSeconds"] = retryAfter });
        }

        return Result.Success(await persistence.CreateAsync(
            session,
            Incident.RouteDeviationType,
            sourceKey,
            now,
            request.TargetItemId,
            request.EventId,
            null,
            cancellationToken));
    }

    private static Result<TripAlertResponse> Failure(string code, string message) =>
        Result.Failure<TripAlertResponse>(code, message);
}