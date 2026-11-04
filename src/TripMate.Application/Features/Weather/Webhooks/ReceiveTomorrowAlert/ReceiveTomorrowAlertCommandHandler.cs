using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Weather.Webhooks.ReceiveTomorrowAlert;

public sealed class ReceiveTomorrowAlertCommandHandler(
    IApplicationDbContext context,
    TimeProvider timeProvider,
    ILogger<ReceiveTomorrowAlertCommandHandler> logger)
    : IRequestHandler<ReceiveTomorrowAlertCommand, Result<ReceiveTomorrowAlertResult>>
{
    private const string ProviderName = "TomorrowIo";

    public async Task<Result<ReceiveTomorrowAlertResult>> Handle(
        ReceiveTomorrowAlertCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RawPayload))
        {
            return Result<ReceiveTomorrowAlertResult>.Failure("Weather.PayloadEmpty", "Payload cannot be empty.");
        }

        string notificationId = ResolveNotificationId(request.RawPayload, request.ProviderNotificationId);

        // Deduplication check
        var existing = await context.WeatherAlertInboxItems
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Provider == ProviderName && item.ProviderNotificationId == notificationId, cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation("Duplicate Tomorrow.io alert received for notification {NotificationId}. Reusing inbox item {InboxId}.",
                notificationId, existing.Id);
            return Result<ReceiveTomorrowAlertResult>.Success(new ReceiveTomorrowAlertResult(existing.Id, existing.Status, IsDuplicate: true));
        }

        var utcNow = timeProvider.GetUtcNow();
        var inboxItem = WeatherAlertInboxItem.CreatePending(
            provider: ProviderName,
            payload: request.RawPayload,
            providerNotificationId: notificationId,
            signature: request.SignatureHeader,
            receivedAtUtc: utcNow);

        context.WeatherAlertInboxItems.Add(inboxItem);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Tomorrow.io alert enqueued into durable inbox item {InboxId} for notification {NotificationId}.",
                inboxItem.Id, notificationId);

            return Result<ReceiveTomorrowAlertResult>.Success(new ReceiveTomorrowAlertResult(inboxItem.Id, inboxItem.Status, IsDuplicate: false));
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Concurrent duplicate delivery detected when saving Tomorrow.io alert {NotificationId}.", notificationId);

            // Re-query to return existing row
            var concurrentExisting = await context.WeatherAlertInboxItems
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Provider == ProviderName && item.ProviderNotificationId == notificationId, cancellationToken);

            if (concurrentExisting is not null)
            {
                return Result<ReceiveTomorrowAlertResult>.Success(new ReceiveTomorrowAlertResult(concurrentExisting.Id, concurrentExisting.Status, IsDuplicate: true));
            }

            throw;
        }
    }

    private static string ResolveNotificationId(string rawPayload, string? explicitId)
    {
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            return explicitId.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idProp.GetString()))
                {
                    return idProp.GetString()!.Trim();
                }

                if (root.TryGetProperty("notificationId", out var notifProp) && notifProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(notifProp.GetString()))
                {
                    return notifProp.GetString()!.Trim();
                }

                if (root.TryGetProperty("eventId", out var eventProp) && eventProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(eventProp.GetString()))
                {
                    return eventProp.GetString()!.Trim();
                }

                if (root.TryGetProperty("event", out var eventObj) && eventObj.ValueKind == JsonValueKind.Object)
                {
                    if (eventObj.TryGetProperty("id", out var evtIdProp) && evtIdProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(evtIdProp.GetString()))
                    {
                        return evtIdProp.GetString()!.Trim();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not valid JSON; fall back to deterministic SHA-256
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
