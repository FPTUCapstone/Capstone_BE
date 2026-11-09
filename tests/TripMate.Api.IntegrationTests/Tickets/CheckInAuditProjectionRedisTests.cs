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