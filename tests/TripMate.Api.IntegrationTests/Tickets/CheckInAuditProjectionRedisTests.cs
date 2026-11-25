using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.CheckIn.Audit;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services.Audit;

namespace TripMate.Api.IntegrationTests.Tickets;

public sealed class CheckInAuditProjectionRedisTests
{
    [RedisFact]
    public async Task ExistingIdenticalSqlProjection_IsAcknowledgedWithoutDuplicateRow()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        await using var multiplexer = await fixture.CreateIndependentMultiplexerAsync();
        string prefix = fixture.CreateUniquePrefix();
        CheckInAuditJournalOptions options = Options(prefix);
        await using var connection = new CheckInAuditRedisConnection(multiplexer, ownsMultiplexer: false);
        var auditEvent = CheckInAuditEvent.Rejected(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Parse("2026-11-05T01:02:03Z"),
            7, 9, 11, "checkin.invalid_qr");
        var journal = new RedisCheckInAuditJournal(connection, options);
        (await journal.AppendAsync(auditEvent, CancellationToken.None))
            .Should().Be(CheckInAuditWriteResult.Confirmed);

        await connection.Database.StreamCreateConsumerGroupAsync(
            options.RejectionStreamKey, options.ConsumerGroup, "0-0", createStream: true);
        await using var provider = Services(prefix).BuildServiceProvider();
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var projector = scope.ServiceProvider.GetRequiredService<CheckInAuditProjector>();
            (await projector.ProjectAsync(auditEvent, CancellationToken.None))
                .Should().Be(CheckInAuditProjectionResult.Projected);
        }

        var metrics = new CheckInAuditJournalMetrics();
        var worker = new CheckInAuditProjectionBackgroundService(
            connection, options, provider.GetRequiredService<IServiceScopeFactory>(), metrics,
            NullLogger<CheckInAuditProjectionBackgroundService>.Instance);

        (await worker.ProcessBatchAsync(CancellationToken.None)).Should().Be(1);
        metrics.Duplicates.Should().Be(1);
        (await connection.Database.StreamPendingAsync(
            options.RejectionStreamKey, options.ConsumerGroup)).PendingMessageCount.Should().Be(0);
        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        var db = (ApplicationDbContext)verificationScope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        (await db.AuditLogs.CountAsync()).Should().Be(1);
    }

    [RedisFact]
    public async Task ConflictingSqlProjection_RemainsPendingAndIsNotAcknowledged()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        await using var multiplexer = await fixture.CreateIndependentMultiplexerAsync();
        string prefix = fixture.CreateUniquePrefix();
        CheckInAuditJournalOptions options = Options(prefix);
        await using var connection = new CheckInAuditRedisConnection(multiplexer, ownsMultiplexer: false);
        var auditEvent = CheckInAuditEvent.Rejected(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Parse("2026-11-05T01:02:03Z"),
            7, 9, 11, "checkin.invalid_qr");
        var journal = new RedisCheckInAuditJournal(connection, options);
        (await journal.AppendAsync(auditEvent, CancellationToken.None))
            .Should().Be(CheckInAuditWriteResult.Confirmed);
        await connection.Database.StreamCreateConsumerGroupAsync(
            options.RejectionStreamKey, options.ConsumerGroup, "0-0", createStream: true);

        await using var provider = Services(prefix).BuildServiceProvider();
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = (ApplicationDbContext)scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            db.AuditLogs.Add(AuditLog.CreateCheckInJournalProjection(
                auditEvent.ActorUserId, auditEvent.ScheduleId + 1, auditEvent.TicketId,
                auditEvent.ScanAttemptId, auditEvent.EventId, auditEvent.ReasonCode!,
                auditEvent.OccurredAtUtc));
            await db.SaveChangesAsync();
        }

        var metrics = new CheckInAuditJournalMetrics();
        var worker = new CheckInAuditProjectionBackgroundService(
            connection, options, provider.GetRequiredService<IServiceScopeFactory>(), metrics,
            NullLogger<CheckInAuditProjectionBackgroundService>.Instance);

        (await worker.ProcessBatchAsync(CancellationToken.None)).Should().Be(0);
        metrics.Conflicts.Should().Be(1);
        (await connection.Database.StreamPendingAsync(
            options.RejectionStreamKey, options.ConsumerGroup)).PendingMessageCount.Should().Be(1);
    }

    [RedisFact]
    public async Task ManyConflictingPendingEvents_DoNotStarveNewHealthyEvent()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        await using var multiplexer = await fixture.CreateIndependentMultiplexerAsync();
        string prefix = fixture.CreateUniquePrefix();
        CheckInAuditJournalOptions options = Options(prefix);
        options.ProjectionBatchSize = 4;
        options.PendingLease = TimeSpan.FromMilliseconds(1);
        await using var connection = new CheckInAuditRedisConnection(multiplexer, ownsMultiplexer: false);
        var journal = new RedisCheckInAuditJournal(connection, options);
        CheckInAuditEvent[] poisonEvents = Enumerable.Range(0, 8)
            .Select(index => CheckInAuditEvent.Rejected(
                Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Parse("2026-11-05T01:02:03Z").AddSeconds(index),
                7, 9, 100 + index, "checkin.invalid_qr"))
            .ToArray();

        foreach (CheckInAuditEvent poisonEvent in poisonEvents)
        {
            (await journal.AppendAsync(poisonEvent, CancellationToken.None))
                .Should().Be(CheckInAuditWriteResult.Confirmed);
        }

        await connection.Database.StreamCreateConsumerGroupAsync(
            options.RejectionStreamKey, options.ConsumerGroup, "0-0", createStream: true);
        (await connection.Database.StreamReadGroupAsync(
            options.RejectionStreamKey, options.ConsumerGroup, "crashed-consumer", ">", 100)).Should().HaveCount(8);

        await using var provider = Services(prefix).BuildServiceProvider();
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = (ApplicationDbContext)scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            foreach (CheckInAuditEvent poisonEvent in poisonEvents)
            {
                db.AuditLogs.Add(AuditLog.CreateCheckInJournalProjection(
                    poisonEvent.ActorUserId, poisonEvent.ScheduleId + 1, poisonEvent.TicketId,
                    poisonEvent.ScanAttemptId, poisonEvent.EventId, poisonEvent.ReasonCode!,
                    poisonEvent.OccurredAtUtc));
            }
            await db.SaveChangesAsync();
        }

        await Task.Delay(20);
        var healthyEvent = CheckInAuditEvent.Rejected(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Parse("2026-11-05T02:02:03Z"),
            7, 9, 999, "checkin.ticket_invalid");
        (await journal.AppendAsync(healthyEvent, CancellationToken.None))
            .Should().Be(CheckInAuditWriteResult.Confirmed);

        var metrics = new CheckInAuditJournalMetrics();
        var worker = new CheckInAuditProjectionBackgroundService(
            connection, options, provider.GetRequiredService<IServiceScopeFactory>(), metrics,
            NullLogger<CheckInAuditProjectionBackgroundService>.Instance);

        (await worker.ProcessBatchAsync(CancellationToken.None)).Should().Be(1);
        metrics.Projected.Should().Be(1);
        metrics.Conflicts.Should().BeGreaterThan(0);
        (await connection.Database.StreamPendingAsync(
            options.RejectionStreamKey, options.ConsumerGroup)).PendingMessageCount.Should().Be(8);

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        var verificationDb = (ApplicationDbContext)verificationScope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        (await verificationDb.AuditLogs.CountAsync(row => row.JournalEventId == healthyEvent.EventId))
            .Should().Be(1);
    }

    [RedisFact]
    public async Task MalformedPoisonEntries_RemainPendingAndDoNotBlockHealthyEventsInSameOrLaterBatch()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        await using var multiplexer = await fixture.CreateIndependentMultiplexerAsync();
        string prefix = fixture.CreateUniquePrefix();
        CheckInAuditJournalOptions options = Options(prefix);
        options.ProjectionBatchSize = 10;
        options.PendingLease = TimeSpan.FromMilliseconds(10);
        await using var connection = new CheckInAuditRedisConnection(multiplexer, ownsMultiplexer: false);
        var journal = new RedisCheckInAuditJournal(connection, options);

        // Inject diverse poison/malformed entries directly into the rejection stream.
        await connection.Database.StreamAddAsync(options.RejectionStreamKey, [new StackExchange.Redis.NameValueEntry("other", "missing-payload")]);
        await connection.Database.StreamAddAsync(options.RejectionStreamKey, [new StackExchange.Redis.NameValueEntry("payload", "{not-json")]);
        await connection.Database.StreamAddAsync(options.RejectionStreamKey, [new StackExchange.Redis.NameValueEntry("payload", "[1,2,3]")]);
        await connection.Database.StreamAddAsync(options.RejectionStreamKey, [new StackExchange.Redis.NameValueEntry("payload", "{\"schemaVersion\":1}")]);
        await connection.Database.StreamAddAsync(options.RejectionStreamKey, [new StackExchange.Redis.NameValueEntry("payload", "{\"schemaVersion\":\"1\",\"actorUserId\":\"abc\"}")]);
        await connection.Database.StreamAddAsync(options.RejectionStreamKey, [new StackExchange.Redis.NameValueEntry("payload",
            $"{{\"eventId\":\"{Guid.NewGuid():N}\",\"scanAttemptId\":\"{Guid.NewGuid():N}\",\"eventType\":\"Admission\",\"occurredAtUtc\":\"2026-11-05T01:02:03Z\",\"actorUserId\":7,\"scheduleId\":9,\"ticketId\":null,\"reasonCode\":null,\"schemaVersion\":1}}")]);

        var healthyOne = CheckInAuditEvent.Rejected(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Parse("2026-11-05T03:00:00Z"),
            7, 9, 1001, "checkin.invalid_qr");
        var healthyTwo = CheckInAuditEvent.Rejected(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Parse("2026-11-05T03:01:00Z"),
            7, 9, 1002, "checkin.already_used");
        (await journal.AppendAsync(healthyOne, CancellationToken.None)).Should().Be(CheckInAuditWriteResult.Confirmed);
        (await journal.AppendAsync(healthyTwo, CancellationToken.None)).Should().Be(CheckInAuditWriteResult.Confirmed);

        await connection.Database.StreamCreateConsumerGroupAsync(
            options.RejectionStreamKey, options.ConsumerGroup, "0-0", createStream: true);

        await using var provider = Services(prefix).BuildServiceProvider();
        var metrics = new CheckInAuditJournalMetrics();
        var logger = new RecordingLogger<CheckInAuditProjectionBackgroundService>();
        var worker = new CheckInAuditProjectionBackgroundService(
            connection, options, provider.GetRequiredService<IServiceScopeFactory>(), metrics, logger);

        (await worker.ProcessBatchAsync(CancellationToken.None)).Should().Be(2);
        metrics.Projected.Should().Be(2);
        metrics.Conflicts.Should().Be(6);
        metrics.ProjectionFailures.Should().Be(0);
        logger.Count(Microsoft.Extensions.Logging.LogLevel.Error).Should().Be(6,
            "initial malformed deliveries must log full ERROR diagnostics");
        logger.Count(Microsoft.Extensions.Logging.LogLevel.Warning).Should().Be(0);
        (await connection.Database.StreamPendingAsync(options.RejectionStreamKey, options.ConsumerGroup))
            .PendingMessageCount.Should().Be(6, "malformed poison entries must stay pending for investigation");
        (await connection.Database.StreamLengthAsync(options.RejectionStreamKey))
            .Should().Be(8, "no unauthorized deletion or trimming is allowed");

        // Second batch / reclaim cycle: wait past PendingLease so poison entries are reclaimed via XAUTOCLAIM,
        // and append a 3rd healthy event. Poison stays pending, emits concise Warnings (no new ERROR flood),
        // never throws out of batch processing, and the healthy event projects cleanly.
        await Task.Delay(30);
        var healthyThree = CheckInAuditEvent.Rejected(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Parse("2026-11-05T03:02:00Z"),
            7, 9, 1003, "checkin.wrong_schedule");
        (await journal.AppendAsync(healthyThree, CancellationToken.None)).Should().Be(CheckInAuditWriteResult.Confirmed);

        (await worker.ProcessBatchAsync(CancellationToken.None)).Should().Be(1);
        metrics.Projected.Should().Be(3);
        metrics.Conflicts.Should().BeGreaterThan(6,
            "Conflicts tracks failed delivery encounters across reclaimed batches");
        metrics.ProjectionFailures.Should().Be(0);
        logger.Count(Microsoft.Extensions.Logging.LogLevel.Error).Should().Be(6,
            "reclaimed poison retries must downgrade to concise Warning instead of repeating ERROR stack traces");
        logger.Count(Microsoft.Extensions.Logging.LogLevel.Warning).Should().BeGreaterThan(0);
        (await connection.Database.StreamPendingAsync(options.RejectionStreamKey, options.ConsumerGroup))
            .PendingMessageCount.Should().Be(6, "poison entries must remain pending and never be silently ACKed");
        (await connection.Database.StreamLengthAsync(options.RejectionStreamKey))
            .Should().Be(9, "reclaim must never delete or trim stream entries");

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        var verificationDb = (ApplicationDbContext)verificationScope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        (await verificationDb.AuditLogs.CountAsync(row =>
            row.JournalEventId == healthyOne.EventId
            || row.JournalEventId == healthyTwo.EventId
            || row.JournalEventId == healthyThree.EventId)).Should().Be(3);
    }

    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> _entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception), exception));

        public int Count(Microsoft.Extensions.Logging.LogLevel level) =>
            _entries.Count(entry => entry.Level == level);
    }

    private static ServiceCollection Services(string databaseName)
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        var services = new ServiceCollection();
        services.AddScoped<IApplicationDbContext>(_ => new ApplicationDbContext(dbOptions));
        services.AddScoped<CheckInAuditProjector>();
        return services;
    }

    private static CheckInAuditJournalOptions Options(string prefix) => new()
    {
        Enabled = true,
        AdmissionStreamKey = $"{prefix}:admissions",
        RejectionStreamKey = $"{prefix}:rejections",
        ReceiptKeyPrefix = $"{prefix}:receipts",
        ConsumerGroup = $"{prefix}:group",
        ConsumerName = "integration-test",
        CommandTimeout = TimeSpan.FromSeconds(3),
        DurabilityTimeout = TimeSpan.FromSeconds(2),
        MaxAppendAttempts = 2,
        ProjectionBatchSize = 10,
        PendingLease = TimeSpan.FromMilliseconds(1),
    };
}