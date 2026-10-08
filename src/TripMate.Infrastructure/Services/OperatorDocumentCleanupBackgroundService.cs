using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.Services;

internal sealed class OperatorDocumentCleanupBackgroundService(
    SqlOperatorDocumentCleanupJournal journal,
    IOperatorDocumentStorage storage,
    IDateTimeProvider clock,
    ILogger<OperatorDocumentCleanupBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("Operator document cleanup batch failed: {ExceptionType}; leased work will retry.",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task<int> ProcessDueBatchAsync(CancellationToken cancellationToken)
    {
        int processed = 0;
        while (processed < BatchSize && !cancellationToken.IsCancellationRequested)
        {
            var now = clock.UtcNow;
            OperatorDocumentCleanupClaim? claim = await journal.ClaimDueAsync(
                now, LeaseDuration, cancellationToken);
            if (claim is null)
            {
                break;
            }

            // A registration may have committed while closing its reservation failed.
            // Never delete a document that the database still references.
            if (await journal.IsRegisteredAsync(claim.ExpectedReference, cancellationToken))
            {
                await journal.CompleteClaimAsync(claim, cancellationToken);
                processed++;
                continue;
            }

            OperatorDocumentStorageDeleteResult? result = null;
            try
            {
                result = await storage.DeleteAsync(claim.PublicId, claim.ContentType, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError("Operator document cleanup attempt {Attempt} failed: {ExceptionType}.",
                    claim.AttemptCount, exception.GetType().Name);
            }

            if (result?.Outcome is OperatorDocumentStorageDeleteOutcome.Deleted or
                OperatorDocumentStorageDeleteOutcome.AlreadyAbsent)
            {
                await journal.CompleteClaimAsync(claim, cancellationToken);
            }
            else
            {
                // Keep the SQL intent indefinitely, with capped backoff. Even a provider
                // "permanent" error can be repaired operationally without losing the asset ID.
                var delay = RetryDelay(claim.AttemptCount);
                await journal.RetryClaimAsync(claim, clock.UtcNow + delay,
                    SafeErrorCode(result?.SafeErrorCode), cancellationToken);
                logger.LogError("Operator document cleanup remains pending after attempt {Attempt}.",
                    claim.AttemptCount);
            }

            processed++;
        }

        return processed;
    }

    private static TimeSpan RetryDelay(int attemptCount) =>
        TimeSpan.FromMinutes(Math.Min(1 << Math.Min(Math.Max(attemptCount - 1, 0), 11), 24 * 60));

    private static string SafeErrorCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && code.Length <= 100 &&
        code.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_')
            ? code : "OPERATOR_DOCUMENT_CLEANUP_FAILED";
}