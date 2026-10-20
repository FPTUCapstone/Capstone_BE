using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Infrastructure.Services;

internal sealed record UserAdministrationNotificationClaim(
    long NotificationId,
    string DestinationEmail,
    string Subject,
    string Body,
    Guid LeaseToken);

internal interface IUserAdministrationNotificationOutboxStore
{
    Task<IReadOnlyList<UserAdministrationNotificationClaim>> ClaimDueAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        long notificationId,
        Guid leaseToken,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> RenewLeaseAsync(
        long notificationId,
        Guid leaseToken,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    Task RetryAsync(
        long notificationId,
        Guid leaseToken,
        DateTimeOffset notBeforeAtUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);

    Task FailAsync(
        long notificationId,
        Guid leaseToken,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken);
}

internal sealed class UserAdministrationNotificationDeliveryProcessor(
    IUserAdministrationNotificationOutboxStore outbox,
    IUserAdministrationNotificationEmailSender sender,
    IDateTimeProvider clock,
    ILogger<UserAdministrationNotificationDeliveryProcessor> logger)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    public async Task<int> ProcessDueBatchAsync(
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var processed = 0;
        while (processed < batchSize && !cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<UserAdministrationNotificationClaim> claims = await outbox.ClaimDueAsync(
                clock.UtcNow,
                leaseDuration,
                batchSize: 1,
                cancellationToken);
            if (claims.Count == 0)
            {
                break;
            }

            await ProcessClaimAsync(claims[0], leaseDuration, cancellationToken);
            processed++;
        }

        return processed;
    }

    private async Task ProcessClaimAsync(
        UserAdministrationNotificationClaim claim,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        EmailDeliveryResult result;
        try
        {
            result = await SendWithLeaseRenewalAsync(
                claim,
                leaseDuration,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Administrative notification delivery failed with {ExceptionType}; scheduling a retry.",
                exception.GetType().Name);
            DateTimeOffset now = clock.UtcNow;
            await outbox.RetryAsync(claim.NotificationId, claim.LeaseToken, now + RetryDelay, now, CancellationToken.None);
            return;
        }

        DateTimeOffset completedAt = clock.UtcNow;
        switch (result.Status)
        {
            case EmailDeliveryStatus.Delivered:
                await outbox.CompleteAsync(claim.NotificationId, claim.LeaseToken, completedAt, cancellationToken);
                break;
            case EmailDeliveryStatus.DefiniteFailure:
                await outbox.FailAsync(claim.NotificationId, claim.LeaseToken, completedAt, cancellationToken);
                break;
            case EmailDeliveryStatus.Unknown:
                await outbox.RetryAsync(
                    claim.NotificationId,
                    claim.LeaseToken,
                    completedAt + RetryDelay,
                    completedAt,
                    cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Unknown email delivery status.");
        }
    }

    private async Task<EmailDeliveryResult> SendWithLeaseRenewalAsync(
        UserAdministrationNotificationClaim claim,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        Task<EmailDeliveryResult> delivery = sender.SendAsync(
            claim.DestinationEmail, claim.Subject, claim.Body, cancellationToken);
        TimeSpan renewalInterval = TimeSpan.FromTicks(Math.Max(1, leaseDuration.Ticks / 3));

        while (!delivery.IsCompleted)
        {
            Task delay = Task.Delay(renewalInterval, cancellationToken);
            if (await Task.WhenAny(delivery, delay) == delivery)
            {
                break;
            }

            DateTimeOffset now = clock.UtcNow;
            bool renewed = await outbox.RenewLeaseAsync(
                claim.NotificationId, claim.LeaseToken, now, now + leaseDuration, cancellationToken);
            if (!renewed)
            {
                logger.LogWarning("Administrative notification {NotificationId} lost its delivery lease while sending.",
                    claim.NotificationId);
                break;
            }
        }

        return await delivery;
    }
}
