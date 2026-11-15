using System.Security.Cryptography;
using System.Text;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Weather.Emergency;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Weather.Webhooks.ReceiveTomorrowAlert;

/// <summary>
/// Stores an authenticated Tomorrow.io webhook in the durable inbox. The documented payload
/// carries no notification id, so a delivery is identified by the SHA-256 of its body; the
/// provider's re-deliveries of the same body therefore collapse into one inbox row.
/// </summary>
public sealed class ReceiveTomorrowAlertCommandHandler(
    IApplicationDbContext context,
    TimeProvider timeProvider,
    ILogger<ReceiveTomorrowAlertCommandHandler> logger)
    : IRequestHandler<ReceiveTomorrowAlertCommand, Result<ReceiveTomorrowAlertResult>>
{
    public async Task<Result<ReceiveTomorrowAlertResult>> Handle(
        ReceiveTomorrowAlertCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RawPayload))
        {
            return Result<ReceiveTomorrowAlertResult>.Failure(
                WeatherAlertErrorCodes.PayloadEmpty, "Payload cannot be empty.");
        }

        string deliveryId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RawPayload)))
            .ToLowerInvariant();

        var existing = await FindAsync(deliveryId, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation("Duplicate Tomorrow.io delivery {DeliveryId}. Reusing inbox item {InboxId}.",
                deliveryId, existing.Id);
            return Result<ReceiveTomorrowAlertResult>.Success(new ReceiveTomorrowAlertResult(existing.Id, existing.Status, IsDuplicate: true));
        }

        var inboxItem = WeatherAlertInboxItem.CreatePending(
            provider: WeatherProviders.TomorrowIo,
            payload: request.RawPayload,
            providerNotificationId: deliveryId,
            receivedAtUtc: timeProvider.GetUtcNow());

        context.WeatherAlertInboxItems.Add(inboxItem);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Tomorrow.io delivery {DeliveryId} enqueued into durable inbox item {InboxId}.",
                deliveryId, inboxItem.Id);

            return Result<ReceiveTomorrowAlertResult>.Success(new ReceiveTomorrowAlertResult(inboxItem.Id, inboxItem.Status, IsDuplicate: false));
        }
        catch (DbUpdateException ex)
        {
            // A concurrent identical delivery won the unique (provider, provider_notification_id) index.
            var concurrentExisting = await FindAsync(deliveryId, cancellationToken);
            if (concurrentExisting is null)
            {
                throw;
            }

            logger.LogWarning(ex, "Concurrent duplicate Tomorrow.io delivery {DeliveryId}.", deliveryId);
            return Result<ReceiveTomorrowAlertResult>.Success(new ReceiveTomorrowAlertResult(concurrentExisting.Id, concurrentExisting.Status, IsDuplicate: true));
        }
    }

    private Task<WeatherAlertInboxItem?> FindAsync(string deliveryId, CancellationToken cancellationToken) =>
        context.WeatherAlertInboxItems
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Provider == WeatherProviders.TomorrowIo
                                         && item.ProviderNotificationId == deliveryId, cancellationToken);
}