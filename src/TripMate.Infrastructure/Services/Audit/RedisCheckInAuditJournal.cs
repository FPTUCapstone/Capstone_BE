using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.CheckIn.Audit;

namespace TripMate.Infrastructure.Services.Audit;

public sealed class RedisCheckInAuditJournal(
    CheckInAuditRedisConnection connection,
    CheckInAuditJournalOptions options,
    ILogger<RedisCheckInAuditJournal>? logger = null)
    : ICheckInAuditJournal
{
    private readonly ILogger<RedisCheckInAuditJournal> _logger =
        logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisCheckInAuditJournal>.Instance;

    public async Task<CheckInAuditWriteResult> AppendAsync(
        CheckInAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (!options.Enabled || !connection.IsConnected)
        {
            return CheckInAuditWriteResult.Unavailable;
        }

        string payload = Serialize(auditEvent);
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        string streamKey = auditEvent.EventType == CheckInAuditEventType.Admission
            ? options.AdmissionStreamKey
            : options.RejectionStreamKey;
        string receiptKey = $"{options.ReceiptKeyPrefix}:{auditEvent.EventType.ToString().ToLowerInvariant()}";

        CheckInAuditWriteResult lastResult = CheckInAuditWriteResult.Uncertain;
        for (int attempt = 1; attempt <= options.MaxAppendAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                lastResult = await AppendOnceAsync(
                    streamKey,
                    receiptKey,
                    auditEvent.EventId.ToString("N"),
                    digest,
                    payload);
                if (lastResult is CheckInAuditWriteResult.Confirmed or CheckInAuditWriteResult.Conflict)
                {
                    return lastResult;
                }
            }
            catch (RedisException exception)
            {
                lastResult = CheckInAuditWriteResult.Uncertain;
                _logger.LogWarning(
                    exception,
                    "Check-in audit append attempt {Attempt}/{MaxAttempts} was uncertain for event {EventId}.",
                    attempt,
                    options.MaxAppendAttempts,
                    auditEvent.EventId);
            }
            catch (TimeoutException exception)
            {
                lastResult = CheckInAuditWriteResult.Uncertain;
                _logger.LogWarning(
                    exception,
                    "Check-in audit append timed out for event {EventId}.",
                    auditEvent.EventId);
            }
        }

        return lastResult;
    }

    private async Task<CheckInAuditWriteResult> AppendOnceAsync(
        RedisKey streamKey,
        RedisKey receiptKey,
        RedisValue eventId,
        RedisValue digest,
        RedisValue payload)
    {
        // IBatch is a non-transactional ordered pipeline. The dedicated single-endpoint
        // multiplexer sends these commands through its one interactive physical connection.
        // CLIENT ID before/after detects any reconnect. WAITAOF intentionally remains outside
        // Lua/MULTI and follows EVAL on the same pipeline/connection.
        IBatch batch = connection.Database.CreateBatch();
        Task<RedisResult> connectionBefore = batch.ExecuteAsync(
            "CLIENT", ["ID"], CommandFlags.DemandMaster);
        Task<RedisResult> append = batch.ScriptEvaluateAsync(
            CheckInAuditAppendLuaScript.Value,
            [streamKey, receiptKey],
            [eventId, digest, payload],
            CommandFlags.DemandMaster);
        Task<RedisResult> waitAof = batch.ExecuteAsync(
            "WAITAOF",
            [1, 0, checked((int)options.DurabilityTimeout.TotalMilliseconds)],
            CommandFlags.DemandMaster);
        Task<RedisResult> connectionAfter = batch.ExecuteAsync(
            "CLIENT", ["ID"], CommandFlags.DemandMaster);

        batch.Execute();
        await Task.WhenAll(connectionBefore, append, waitAof, connectionAfter);

        long beforeId = ParseInteger(await connectionBefore);
        CheckInAuditAppendDisposition disposition = ParseAppend(await append);
        long localFsyncCount = ParseWaitAof(await waitAof);
        long afterId = ParseInteger(await connectionAfter);

        return CheckInAuditBatchEvidence.Evaluate(
            beforeId,
            afterId,
            disposition,
            localFsyncCount);
    }

    private static CheckInAuditAppendDisposition ParseAppend(RedisResult result)
    {
        RedisResult[] values = (RedisResult[])result!;
        string disposition = values.Length > 0
            ? (string?)values[0] ?? string.Empty
            : string.Empty;
        return disposition switch
        {
            "created" => CheckInAuditAppendDisposition.Created,
            "existing" => CheckInAuditAppendDisposition.Existing,
            "conflict" => CheckInAuditAppendDisposition.Conflict,
            _ => throw new InvalidDataException("Unexpected check-in audit append response."),
        };
    }

    private static long ParseWaitAof(RedisResult result)
    {
        RedisResult[] values = (RedisResult[])result!;
        return values.Length > 0 ? ParseInteger(values[0]) : 0;
    }

    private static long ParseInteger(RedisResult result) =>
        long.Parse((string?)result ?? "0", CultureInfo.InvariantCulture);

    private static string Serialize(CheckInAuditEvent auditEvent) =>
        JsonSerializer.Serialize(new
        {
            eventId = auditEvent.EventId.ToString("N"),
            scanAttemptId = auditEvent.ScanAttemptId.ToString("N"),
            eventType = auditEvent.EventType.ToString(),
            occurredAtUtc = auditEvent.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            actorUserId = auditEvent.ActorUserId,
            scheduleId = auditEvent.ScheduleId,
            ticketId = auditEvent.TicketId,
            reasonCode = auditEvent.ReasonCode,
            schemaVersion = auditEvent.SchemaVersion,
        });
}