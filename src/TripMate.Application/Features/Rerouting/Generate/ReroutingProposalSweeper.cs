using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Generate;

/// <summary>
/// The housekeeping of the scan job (spec 2.2 and 4.1): marks overdue proposals Expired and retries incidents that
/// still have no proposal. Correctness never depends on it: Accept, Reject and GET each check the TTL themselves.
/// </summary>
public interface IReroutingProposalSweeper
{
    Task SweepAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

internal sealed class ReroutingProposalSweeper(
    IApplicationDbContext dbContext,
    IReroutingProposalGenerator generator,
    IOptions<ReroutingOptions> options,
    ILogger<ReroutingProposalSweeper>? logger = null)
    : IReroutingProposalSweeper
{
    public const int MaxRetriesPerScan = 20;

    private readonly ReroutingOptions options = options.Value;

    public async Task SweepAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await RunAsync("expire overdue proposals", () => ExpireOverdueAsync(nowUtc, cancellationToken), cancellationToken);
        await RunAsync("retry missing proposals", () => RetryMissingAsync(nowUtc, cancellationToken), cancellationToken);
    }

    private async Task ExpireOverdueAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var oldestStillOpen = nowUtc - options.ProposalTtl;
        var overdue = await dbContext.ReroutingEvents
            .Where(proposal => proposal.Status == ReroutingEvent.StatusProposed
                && (proposal.ProposedAtUtc < oldestStillOpen || proposal.Session.EndedAtUtc != null))
            .ToArrayAsync(cancellationToken);
        if (overdue.Length == 0)
        {
            return;
        }

        foreach (var proposal in overdue)
        {
            proposal.Expire(nowUtc);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RetryMissingAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var windowStart = nowUtc - options.ProposalRetryWindow;
        var overstayPrefix = TripAlertResponse.OverstaySourcePrefix;
        var incidentIds = await dbContext.Incidents.AsNoTracking()
            .Where(incident => incident.ResolvedAtUtc == null
                && incident.DetectedAtUtc >= windowStart
                && ReroutingTriggers.IncidentTypes.Contains(incident.IncidentType)
                && incident.AffectedItineraryItemId != null
                && (incident.SourceKey == null || !incident.SourceKey.StartsWith(overstayPrefix))
                && !dbContext.ReroutingEvents.Any(proposal => proposal.IncidentId == incident.Id))
            .OrderBy(incident => incident.DetectedAtUtc)
            .ThenBy(incident => incident.Id)
            .Take(MaxRetriesPerScan)
            .Select(incident => incident.Id)
            .ToArrayAsync(cancellationToken);

        foreach (var incidentId in incidentIds)
        {
            await RunAsync(
                $"retry incident {incidentId}",
                () => generator.TryGenerateAsync(incidentId, nowUtc, cancellationToken),
                cancellationToken);
        }
    }

    private async Task RunAsync(string step, Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "UC-15 sweep step '{Step}' failed; the scan continues.", step);
        }
    }
}