using System.Diagnostics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Scheduling.Explanation;

/// <summary>What one background explanation ended with: the logged outcome and how many items got an AI sentence.</summary>
public sealed record BackgroundItineraryExplanationResult(string Outcome, int UpdatedItems);

/// <summary>Writes the AI explanation of one queued itinerary; the hosted worker calls it once per itinerary.</summary>
public interface IBackgroundItineraryExplanationProcessor
{
    Task<BackgroundItineraryExplanationResult> ProcessAsync(
        ItineraryExplanationJob job,
        CancellationToken cancellationToken);
}

/// <summary>
/// Writes the AI explanation of one queued itinerary (specs/UC-10-background-explanation-spec.md, B2-B5). The
/// provider gets the background time budget, the answer is validated like the inline one, and an item is
/// overwritten only while it still holds the template sentence saved when the itinerary was created. Every
/// failure keeps the template sentences and is only logged; a cancellation by the host is the one thing rethrown.
/// </summary>
public sealed class BackgroundItineraryExplanationProcessor(
    IApplicationDbContext dbContext,
    IItineraryExplanationProvider provider,
    ItineraryExplanationExecutionOptions options,
    ILogger<BackgroundItineraryExplanationProcessor>? logger = null)
    : IBackgroundItineraryExplanationProcessor
{
    public async Task<BackgroundItineraryExplanationResult> ProcessAsync(
        ItineraryExplanationJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        long startedAt = Stopwatch.GetTimestamp();
        IReadOnlyDictionary<int, string>? explanations = null;
        string outcome;
        bool timedOut = false;

        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(options.BackgroundTimeout);
            try
            {
                Result<ItineraryExplanationResult> answer = await provider.ExplainAsync(
                    job.Input,
                    options.BackgroundTimeout,
                    budget.Token);
                if (answer.IsSuccess
                    && ItineraryExplanationValidator.TryValidate(
                        job.Input,
                        answer.Value,
                        out IReadOnlyDictionary<int, string> validated))
                {
                    explanations = validated;
                    outcome = ItineraryExplanationOutcomes.Success;
                }
                else
                {
                    outcome = answer.IsFailure
                        ? ItineraryExplanationOutcomes.FromErrorCode(answer.ErrorCode)
                        : ItineraryExplanationOutcomes.InvalidResponse;
                    timedOut = answer.IsFailure && answer.ErrorCode == ExplanationProviderErrorCodes.Timeout;
                }
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested
                && budget.IsCancellationRequested)
            {
                outcome = ItineraryExplanationOutcomes.Timeout;
                timedOut = true;
            }
        }

        int updatedItems = 0;
        Exception? saveFailure = null;
        if (explanations is not null)
        {
            (outcome, updatedItems, saveFailure) = await SaveAsync(job, explanations, cancellationToken);
        }

        Log(job, outcome, updatedItems, timedOut, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, saveFailure);
        return new BackgroundItineraryExplanationResult(outcome, updatedItems);
    }

    private async Task<(string Outcome, int UpdatedItems, Exception? Failure)> SaveAsync(
        ItineraryExplanationJob job,
        IReadOnlyDictionary<int, string> explanations,
        CancellationToken cancellationToken)
    {
        try
        {
            long[] itemIds = job.Items.Select(item => item.ItineraryItemId).ToArray();
            List<ItineraryItem> stored = await dbContext.ItineraryItems
                .Where(item => item.ItineraryId == job.ItineraryId && itemIds.Contains(item.Id))
                .ToListAsync(cancellationToken);

            int updatedItems = 0;
            foreach (ItineraryExplanationJobItem queued in job.Items)
            {
                ItineraryItem? item = stored.FirstOrDefault(candidate => candidate.Id == queued.ItineraryItemId);

                // B4: an item that is gone, moved, or no longer holds the template sentence saved at creation was
                // changed after the itinerary was queued. It is left exactly as it is.
                if (item is null
                    || item.SequenceNo != queued.SequenceNo
                    || !string.Equals(item.FriendlyExplanation, queued.TemplateText, StringComparison.Ordinal)
                    || !explanations.TryGetValue(queued.SequenceNo, out string? explanation))
                {
                    continue;
                }

                item.AttachFriendlyExplanation(explanation);
                updatedItems++;
            }

            if (updatedItems == 0)
            {
                return (ItineraryExplanationOutcomes.Stale, 0, null);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return (ItineraryExplanationOutcomes.Success, updatedItems, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Nothing half-saved may ride along with a later save of this context.
            dbContext.ClearTrackedEntities();
            return (ItineraryExplanationOutcomes.PersistenceFailed, 0, exception);
        }
    }

    private void Log(
        ItineraryExplanationJob job,
        string outcome,
        int updatedItems,
        bool timedOut,
        double latencyMs,
        Exception? failure)
    {
        if (logger is null)
        {
            return;
        }

        // Ids, counts and outcome names only: no place name and no sentence reaches the log.
        bool succeeded = outcome == ItineraryExplanationOutcomes.Success;
        LogLevel level = succeeded || outcome == ItineraryExplanationOutcomes.Stale
            ? LogLevel.Information
            : LogLevel.Warning;
        logger.Log(
            level,
            failure,
            "Itinerary explanation (background) provider={Provider}; itineraryId={ItineraryId}; itemCount={ItemCount}; updatedItems={UpdatedItems}; latencyMs={LatencyMs:F0}; outcome={Outcome}; fallbackUsed={FallbackUsed}; timedOut={TimedOut}.",
            provider.GetType().Name,
            job.ItineraryId,
            job.Items.Count,
            updatedItems,
            latencyMs,
            outcome,
            !succeeded,
            timedOut);
    }
}