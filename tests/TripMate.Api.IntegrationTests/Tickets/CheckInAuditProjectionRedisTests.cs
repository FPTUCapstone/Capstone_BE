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