using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;

namespace TripMate.Infrastructure.Services;

internal sealed record TourMediaCleanupClaim(
    long Id,
    string CloudinaryPublicId,
    Guid LeaseToken,
    int AttemptCount,
    int MaxAttempts);

internal interface ITourMediaCleanupOutboxStore
{
    Task<IReadOnlyList<TourMediaCleanupClaim>> ClaimDueAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken);

    Task CompleteAsync(long id, Guid leaseToken, DateTimeOffset completedAtUtc, CancellationToken cancellationToken);

    Task RetryAsync(
        long id,
        Guid leaseToken,
        string safeErrorCode,
        DateTimeOffset notBeforeUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);

    Task ExhaustAsync(
        long id,
        Guid leaseToken,
        string safeErrorCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);
}

internal sealed class TourMediaCleanupProcessor(
    ITourMediaCleanupOutboxStore outbox,
    ITourMediaStorage storage,
    IDateTimeProvider clock,
    ILogger<TourMediaCleanupProcessor> logger)
{
    private const string UnknownProviderFailureCode = "TOUR_MEDIA_CLEANUP_UNCLASSIFIED";

    public async Task<int> ProcessDueBatchAsync(
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TourMediaCleanupClaim> claims = await outbox.ClaimDueAsync(
            clock.UtcNow,
            leaseDuration,
            batchSize,
            cancellationToken);

        await Parallel.ForEachAsync(
            claims,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = batchSize,
                CancellationToken = cancellationToken,
            },
            async (claim, token) => await ProcessClaimAsync(claim, token));

        return claims.Count;
    }

    private async ValueTask ProcessClaimAsync(
        TourMediaCleanupClaim claim,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TourMediaStorageDeleteResult result = await storage.DestroyAsync(
            claim.CloudinaryPublicId,
            cancellationToken);
        var now = clock.UtcNow;

        switch (result.Outcome)
        {
            case TourMediaStorageDeleteOutcome.Deleted:
            case TourMediaStorageDeleteOutcome.AlreadyAbsent:
                await outbox.CompleteAsync(claim.Id, claim.LeaseToken, now, cancellationToken);
                break;

            case TourMediaStorageDeleteOutcome.TransientFailure:
                if (claim.AttemptCount >= claim.MaxAttempts)
                {
                    await outbox.ExhaustAsync(
                        claim.Id,
                        claim.LeaseToken,
                        SafeErrorCode(result.SafeErrorCode),
                        now,
                        cancellationToken);
                }
                else
                {
                    DateTimeOffset retryAt = now + TourMediaCleanupRetryPolicy.GetDelayForAttempt(
                        claim.AttemptCount);
                    await outbox.RetryAsync(
                        claim.Id,
                        claim.LeaseToken,
                        SafeErrorCode(result.SafeErrorCode),
                        retryAt,
                        now,
                        cancellationToken);
                }

                break;

            case TourMediaStorageDeleteOutcome.PermanentFailure:
                await outbox.ExhaustAsync(
                    claim.Id,
                    claim.LeaseToken,
                    SafeErrorCode(result.SafeErrorCode),
                    now,
                    cancellationToken);
                break;

            default:
                await outbox.ExhaustAsync(
                    claim.Id,
                    claim.LeaseToken,
                    UnknownProviderFailureCode,
                    now,
                    cancellationToken);
                logger.LogWarning(
                    "Tour media cleanup returned an unclassified outcome for outbox item {OutboxItemId}.",
                    claim.Id);
                break;
        }
    }

    private static string SafeErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100)
        {
            return UnknownProviderFailureCode;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character) && character != '_')
            {
                return UnknownProviderFailureCode;
            }
        }

        return value;
    }
}