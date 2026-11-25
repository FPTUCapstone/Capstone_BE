using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Engine;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Generate;

/// <summary>
/// Creates the rerouting proposal for a trip-alert incident. Called after the incident and its Notification have
/// committed, so a failure here can never lose or delay the alert (spec 4.1 rule 4).
/// </summary>
public interface IReroutingProposalGenerator
{
    /// <returns>True when a proposal was stored; false when there is nothing to propose or generation failed.</returns>
    Task<bool> TryGenerateAsync(long incidentId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

/// <summary>The incident types that may get a proposal (spec 4.1 rule 1).</summary>
internal static class ReroutingTriggers
{
    public static readonly string[] IncidentTypes =
    [
        Incident.SevereWeatherType,
        Incident.PoiClosureType,
        Incident.ScheduleDelayType,
        Incident.RouteDeviationType,
    ];

    /// <summary>Not an overstay alert on the exploring stop: that one has no Pending stop to change.</summary>
    public static bool IsEligible(Incident incident) =>
        IncidentTypes.Contains(incident.IncidentType)
        && incident.AffectedItineraryItemId.HasValue
        && incident.SourceKey?.StartsWith(TripAlertResponse.OverstaySourcePrefix, StringComparison.Ordinal) != true;
}

internal sealed class ReroutingProposalGenerator(
    IApplicationDbContext dbContext,
    ReroutingProposalBuilder builder,
    IOptions<ReroutingOptions> options,
    ILogger<ReroutingProposalGenerator>? logger = null)
    : IReroutingProposalGenerator
{
    private readonly ReroutingOptions options = options.Value;

    public async Task<bool> TryGenerateAsync(long incidentId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ReroutingEvent? added = null;
        try
        {
            var incident = await dbContext.Incidents
                .Include(candidate => candidate.Session)
                .SingleOrDefaultAsync(candidate => candidate.Id == incidentId, cancellationToken);
            if (incident is null || !ReroutingTriggers.IsEligible(incident) || !IsOpen(incident.Session, nowUtc))
            {
                return false;
            }

            if (await dbContext.ReroutingEvents.AnyAsync(proposal => proposal.IncidentId == incidentId, cancellationToken))
            {
                return false;
            }

            // At most one unexpired proposal per session. A proposal past its TTL no longer blocks, even if the
            // sweep has not marked it Expired yet.
            var oldestStillOpen = nowUtc - options.ProposalTtl;
            if (await dbContext.ReroutingEvents.AnyAsync(
                    proposal => proposal.SessionId == incident.SessionId
                        && proposal.Status == ReroutingEvent.StatusProposed
                        && proposal.ProposedAtUtc >= oldestStillOpen,
                    cancellationToken))
            {
                return false;
            }

            var snapshot = await builder.BuildAsync(incidentId, nowUtc, cancellationToken);
            if (snapshot is null)
            {
                return false;
            }

            added = ReroutingEvent.Propose(
                incident, incident.Session, ReroutingProposalSnapshot.Serialize(snapshot), nowUtc);
            dbContext.ReroutingEvents.Add(added);
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "UC-15 could not create a rerouting proposal for incident {IncidentId}; the alert is unaffected.",
                incidentId);
            if (added is not null)
            {
                // A failed insert must not stay tracked, or the next save of this scope would repeat it.
                dbContext.ReroutingEvents.Remove(added);
            }

            return false;
        }
    }

    private static bool IsOpen(TripSession session, DateTimeOffset nowUtc) =>
        session.EndedAtUtc is null
        && (session.ExpiresAtUtc is null || session.ExpiresAtUtc > nowUtc)
        && session.FsmState is TripSession.NavigatingState or TripSession.ExploringState;
}