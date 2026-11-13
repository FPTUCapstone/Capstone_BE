using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using StackExchange.Redis;

using TripMate.Application.Features.Operator.CheckIn.Audit;

namespace TripMate.Infrastructure.Services.Audit;

internal sealed class CheckInAuditProjectionBackgroundService(
    CheckInAuditRedisConnection connection, CheckInAuditJournalOptions options,
    IServiceScopeFactory scopeFactory, CheckInAuditJournalMetrics metrics,
    ILogger<CheckInAuditProjectionBackgroundService> logger) : BackgroundService
{
    private RedisValue _pendingRecoveryCursor = "0-0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool groupReady = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!groupReady)
                {
                    await EnsureGroupAsync();
                    groupReady = true;
                }
                if (await ProcessBatchAsync(stoppingToken) == 0) await Task.Delay(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                groupReady = false;
                metrics.RecordFailure();
                logger.LogError(exception, "Check-in audit projection sweep failed; messages remain unacknowledged.");
                try { await Task.Delay(options.PollInterval, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        StreamEntry[] entries = await ReadEntriesAsync();
        int processed = 0;
        foreach (StreamEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckInAuditEvent auditEvent;
            try
            {
                auditEvent = Parse(entry);
                if (auditEvent.EventType != CheckInAuditEventType.Rejected) throw new InvalidDataException("Admission appeared in outcome stream.");
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidDataException or ArgumentException)
            {
                metrics.RecordConflict();
                logger.LogError(exception, "Malformed audit event {StreamEntryId}; leaving pending.", entry.Id);
                continue;
            }

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            var projector = scope.ServiceProvider.GetRequiredService<CheckInAuditProjector>();
            CheckInAuditProjectionResult result = await projector.ProjectAsync(auditEvent, cancellationToken);
            if (result == CheckInAuditProjectionResult.Conflict)
            {
                metrics.RecordConflict();
                logger.LogError("Conflicting projection for {EventId}; leaving {StreamEntryId} pending.", auditEvent.EventId, entry.Id);
                continue;
            }
            long acknowledged = await connection.Database.StreamAcknowledgeAsync(
                options.RejectionStreamKey, options.ConsumerGroup, entry.Id, CommandFlags.DemandMaster);
            if (!RecordAcknowledgement(acknowledged, metrics))
            {
                logger.LogWarning(
                    "SQL projection completed but Redis acknowledged {AcknowledgedCount} entries for {StreamEntryId}; " +
                    "the outcome is not reported as processed.",
                    acknowledged,
                    entry.Id);
                continue;
            }
            if (result == CheckInAuditProjectionResult.Projected) metrics.RecordProjected(); else metrics.RecordDuplicate();
            processed++;
        }
        return processed;
    }

    private async Task<StreamEntry[]> ReadEntriesAsync()
    {
        // Recovery receives at most half the configured capacity. A fresh read is performed on
        // every sweep so a large poison/conflict PEL cannot prevent newly appended outcomes from
        // being projected. The cursor advances across the PEL instead of rescanning from 0-0.
        int pendingLimit = Math.Max(1, options.ProjectionBatchSize / 2);
        StreamAutoClaimResult claimed = await connection.Database.StreamAutoClaimAsync(
            options.RejectionStreamKey, options.ConsumerGroup, options.ConsumerName,
            checked((long)options.PendingLease.TotalMilliseconds), _pendingRecoveryCursor,
            pendingLimit, CommandFlags.DemandMaster);
        _pendingRecoveryCursor = claimed.NextStartId.IsNullOrEmpty
            ? "0-0"
            : claimed.NextStartId;

        int freshLimit = Math.Max(1, options.ProjectionBatchSize - claimed.ClaimedEntries.Length);
        StreamEntry[] fresh = await connection.Database.StreamReadGroupAsync(
            options.RejectionStreamKey, options.ConsumerGroup, options.ConsumerName, ">",
            freshLimit, noAck: false, CommandFlags.DemandMaster);
        return claimed.ClaimedEntries.Length == 0
            ? fresh
            : [.. claimed.ClaimedEntries, .. fresh];
    }

    internal static bool RecordAcknowledgement(long acknowledged, CheckInAuditJournalMetrics metrics)
    {
        if (acknowledged == 1)
        {
            return true;
        }

        metrics.RecordAcknowledgementMiss();
        return false;
    }

    private async Task EnsureGroupAsync()
    {
        try
        {
            await connection.Database.StreamCreateConsumerGroupAsync(
                options.RejectionStreamKey, options.ConsumerGroup, "0-0", createStream: true, CommandFlags.DemandMaster);
        }
        catch (RedisServerException exception) when (exception.Message.Contains("BUSYGROUP", StringComparison.Ordinal)) { }
    }

    internal static CheckInAuditEvent Parse(StreamEntry entry)
    {
        string? payload = entry.Values.FirstOrDefault(value => value.Name == "payload").Value;
        if (string.IsNullOrWhiteSpace(payload)) throw new InvalidDataException("Payload missing.");
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != CheckInAuditEvent.CurrentSchemaVersion) throw new InvalidDataException("Unsupported schema.");
        Guid eventId = Guid.ParseExact(root.GetProperty("eventId").GetString()!, "N");
        Guid attemptId = Guid.ParseExact(root.GetProperty("scanAttemptId").GetString()!, "N");
        if (!Enum.TryParse(root.GetProperty("eventType").GetString(), out CheckInAuditEventType type)) throw new InvalidDataException("Invalid event type.");
        DateTimeOffset occurred = DateTimeOffset.Parse(root.GetProperty("occurredAtUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        long actor = root.GetProperty("actorUserId").GetInt64();
        long schedule = root.GetProperty("scheduleId").GetInt64();
        JsonElement ticketElement = root.GetProperty("ticketId");
        long? ticket = ticketElement.ValueKind == JsonValueKind.Null ? null : ticketElement.GetInt64();
        string? reason = root.GetProperty("reasonCode").GetString();
        return type == CheckInAuditEventType.Rejected
            ? CheckInAuditEvent.Rejected(eventId, attemptId, occurred, actor, schedule, ticket, reason!)
            : CheckInAuditEvent.Admission(eventId, attemptId, occurred, actor, schedule);
    }
}