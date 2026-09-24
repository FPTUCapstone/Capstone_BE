using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Infrastructure.Email;

internal sealed class PasswordResetEmailDeliveryService(
    PasswordResetEmailQueue queue,
    IPasswordResetStateStore stateStore,
    IPasswordResetAccountLock accountLock,
    IServiceScopeFactory scopeFactory,
    ILogger<PasswordResetEmailDeliveryService> logger)
    : BackgroundService
{
    private const int ConsumerCount = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = Enumerable.Range(0, ConsumerCount)
            .Select(_ => ProcessQueueAsync(stoppingToken));

        await Task.WhenAll(consumers);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var delivery in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await DeliverAsync(delivery, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Password reset email worker failed for generation {Generation}.",
                        delivery.Generation);
                    await InvalidateAfterFailureAsync(delivery);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
    }

    private async Task DeliverAsync(PasswordResetEmailDelivery delivery, CancellationToken cancellationToken)
    {
        var shouldDeliver = await accountLock.ExecuteAsync(
            delivery.UserId,
            _ =>
            {
                var current = stateStore.GetCurrent(delivery.UserId);
                return Task.FromResult(current is not null
                    && current.Generation == delivery.Generation
                    && current.DeliveryState == PasswordResetDeliveryState.Pending);
            },
            cancellationToken);

        if (!shouldDeliver)
        {
            return;
        }

        EmailDeliveryResult result;
        using (var scope = scopeFactory.CreateScope())
        {
            var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            result = await emailSender.SendPasswordResetOtpAsync(
                delivery.DestinationEmail,
                delivery.Otp,
                cancellationToken);
        }

        await accountLock.ExecuteAsync(
            delivery.UserId,
            _ =>
            {
                if (result.Status == EmailDeliveryStatus.Delivered)
                {
                    stateStore.TryTransitionDelivery(
                        delivery.UserId,
                        delivery.Generation,
                        PasswordResetDeliveryState.Sent);
                }
                else
                {
                    stateStore.TryInvalidate(delivery.UserId, delivery.Generation);
                }

                return Task.CompletedTask;
            },
            cancellationToken);
    }

    private async Task InvalidateAfterFailureAsync(PasswordResetEmailDelivery delivery)
    {
        try
        {
            await InvalidateAsync(delivery, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Password reset email worker could not invalidate generation {Generation} after delivery failure.",
                delivery.Generation);
        }
    }

    private Task InvalidateAsync(PasswordResetEmailDelivery delivery, CancellationToken cancellationToken) =>
        accountLock.ExecuteAsync(
            delivery.UserId,
            _ =>
            {
                stateStore.TryInvalidate(delivery.UserId, delivery.Generation);
                return Task.CompletedTask;
            },
            cancellationToken);
}