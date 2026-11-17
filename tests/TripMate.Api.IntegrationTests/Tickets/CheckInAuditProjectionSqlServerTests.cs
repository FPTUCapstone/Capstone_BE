using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.CheckIn.Audit;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services.Audit;

using DependencyInjectionServiceProvider = Microsoft.Extensions.DependencyInjection.ServiceProvider;

namespace TripMate.Api.IntegrationTests.Tickets;

[Trait("Category", "SqlServer")]
public sealed class CheckInAuditProjectionSqlServerTests
{
    [SqlServerRedisFact]
    public async Task SqlOutage_GrowsPendingBacklog_ThenRecoveryProjectsAndAcknowledgesAll()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedActorAsync(database);
        await using var redis = await RedisTestFixture.CreateAsync();
        await using var multiplexer = await redis.CreateIndependentMultiplexerAsync();
        string prefix = redis.CreateUniquePrefix();
        CheckInAuditJournalOptions options = Options(prefix);
        await using var connection = new CheckInAuditRedisConnection(multiplexer, ownsMultiplexer: false);
        var journal = new RedisCheckInAuditJournal(connection, options);
        CheckInAuditEvent[] events = Enumerable.Range(0, 25)
            .Select(index => Rejection(index + 1))
            .ToArray();

        foreach (CheckInAuditEvent auditEvent in events)
        {
            (await journal.AppendAsync(auditEvent, CancellationToken.None))
                .Should().Be(CheckInAuditWriteResult.Confirmed);
        }

        await connection.Database.StreamCreateConsumerGroupAsync(
            options.RejectionStreamKey, options.ConsumerGroup, "0-0", createStream: true);
        await using DependencyInjectionServiceProvider unavailableProvider = Services(UnavailableConnection(database.ConnectionString));
        var outageWorker = Worker(connection, options, unavailableProvider, new CheckInAuditJournalMetrics());

        Func<Task> outage = async () => await outageWorker.ProcessBatchAsync(CancellationToken.None);
        await outage.Should().ThrowAsync<Exception>();
        StreamPendingInfo duringOutage = await connection.Database.StreamPendingAsync(
            options.RejectionStreamKey, options.ConsumerGroup);
        duringOutage.PendingMessageCount.Should().Be(options.ProjectionBatchSize,
            "the delivered batch must remain pending while SQL is unavailable");
        (await connection.Database.StreamLengthAsync(options.RejectionStreamKey))
            .Should().Be(events.Length, "no stream trim or expiry is allowed");

        await Task.Delay(options.PendingLease + TimeSpan.FromMilliseconds(20));
        await using DependencyInjectionServiceProvider recoveredProvider = Services(database.ConnectionString);
        var metrics = new CheckInAuditJournalMetrics();
        var recoveredWorker = Worker(connection, options, recoveredProvider, metrics);
        for (int pass = 0; pass < 10 && metrics.Projected < events.Length; pass++)
        {
            await recoveredWorker.ProcessBatchAsync(CancellationToken.None);
            await Task.Delay(options.PendingLease + TimeSpan.FromMilliseconds(5));
        }

        metrics.Projected.Should().Be(events.Length);
        (await connection.Database.StreamPendingAsync(
            options.RejectionStreamKey, options.ConsumerGroup)).PendingMessageCount.Should().Be(0);
        await using ApplicationDbContext verification = database.CreateDbContext();
        (await verification.AuditLogs.CountAsync(row => row.JournalEventId != null))
            .Should().Be(events.Length);
        (await connection.Database.StreamLengthAsync(options.RejectionStreamKey))
            .Should().Be(events.Length, "ACK must not delete retained journal entries");
    }

    [SqlServerRedisFact]
    public async Task ExistingIdenticalProjection_IsAcknowledged_ButConflictStaysPending()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedActorAsync(database);
        await using var redis = await RedisTestFixture.CreateAsync();
        await using var multiplexer = await redis.CreateIndependentMultiplexerAsync();
        string prefix = redis.CreateUniquePrefix();
        CheckInAuditJournalOptions options = Options(prefix);
        await using var connection = new CheckInAuditRedisConnection(multiplexer, ownsMultiplexer: false);
        var journal = new RedisCheckInAuditJournal(connection, options);
        CheckInAuditEvent duplicate = Rejection(101);
        CheckInAuditEvent conflict = Rejection(102);
        (await journal.AppendAsync(duplicate, CancellationToken.None)).Should().Be(CheckInAuditWriteResult.Confirmed);
        (await journal.AppendAsync(conflict, CancellationToken.None)).Should().Be(CheckInAuditWriteResult.Confirmed);
        await connection.Database.StreamCreateConsumerGroupAsync(
            options.RejectionStreamKey, options.ConsumerGroup, "0-0", createStream: true);

        await using (ApplicationDbContext seed = database.CreateDbContext())
        {
            seed.AuditLogs.Add(AuditLog.CreateCheckInJournalProjection(
                duplicate.ActorUserId, duplicate.ScheduleId, duplicate.TicketId,
                duplicate.ScanAttemptId, duplicate.EventId, duplicate.ReasonCode!, duplicate.OccurredAtUtc));
            seed.AuditLogs.Add(AuditLog.CreateCheckInJournalProjection(
                conflict.ActorUserId, conflict.ScheduleId + 1, conflict.TicketId,
                conflict.ScanAttemptId, conflict.EventId, conflict.ReasonCode!, conflict.OccurredAtUtc));
            await seed.SaveChangesAsync();
        }

        await using DependencyInjectionServiceProvider provider = Services(database.ConnectionString);
        var metrics = new CheckInAuditJournalMetrics();
        var worker = Worker(connection, options, provider, metrics);
        (await worker.ProcessBatchAsync(CancellationToken.None)).Should().Be(1);

        metrics.Duplicates.Should().Be(1);
        metrics.Conflicts.Should().Be(1);
        (await connection.Database.StreamPendingAsync(
            options.RejectionStreamKey, options.ConsumerGroup)).PendingMessageCount.Should().Be(1,
                "a semantic conflict must never be acknowledged");
    }

    private static CheckInAuditProjectionBackgroundService Worker(
        CheckInAuditRedisConnection connection,
        CheckInAuditJournalOptions options,
        DependencyInjectionServiceProvider provider,
        CheckInAuditJournalMetrics metrics) =>
        new(connection, options, provider.GetRequiredService<IServiceScopeFactory>(), metrics,
            NullLogger<CheckInAuditProjectionBackgroundService>.Instance);

    private static DependencyInjectionServiceProvider Services(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddScoped<IApplicationDbContext>(_ =>
        {
            var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            return new ApplicationDbContext(dbOptions);
        });
        services.AddScoped<CheckInAuditProjector>();
        return services.BuildServiceProvider();
    }

    private static string UnavailableConnection(string validConnection)
    {
        var builder = new SqlConnectionStringBuilder(validConnection)
        {
            DataSource = "127.0.0.1,1",
            ConnectTimeout = 1,
            Pooling = false,
        };
        return builder.ConnectionString;
    }

    private static Task SeedActorAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            SET IDENTITY_INSERT dbo.Users ON;
            INSERT dbo.Users(user_id, role, email, full_name, status)
            VALUES (7, 'TourOperator', 'tm89-audit@test.local', N'TM-89 Audit Operator', 'Active');
            SET IDENTITY_INSERT dbo.Users OFF;
            """);

    private static CheckInAuditEvent Rejection(int discriminator) =>
        CheckInAuditEvent.Rejected(
            Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.Parse("2026-11-05T01:02:03Z").AddMilliseconds(discriminator),
            7, 9, 11, "checkin.invalid_qr");

    private static CheckInAuditJournalOptions Options(string prefix) => new()
    {
        Enabled = true,
        AdmissionStreamKey = $"{prefix}:admissions",
        RejectionStreamKey = $"{prefix}:rejections",
        ReceiptKeyPrefix = $"{prefix}:receipts",
        ConsumerGroup = $"{prefix}:group",
        ConsumerName = "sql-integration-test",
        CommandTimeout = TimeSpan.FromSeconds(3),
        DurabilityTimeout = TimeSpan.FromSeconds(2),
        MaxAppendAttempts = 2,
        ProjectionBatchSize = 20,
        PendingLease = TimeSpan.FromMilliseconds(25),
    };
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SqlServerRedisFactAttribute : FactAttribute
{
    public SqlServerRedisFactAttribute()
    {
        string[] required =
        [
            SqlServerTestDatabase.ConnectionStringEnvironmentVariable,
            RedisTestFixture.ConnectionStringEnvironmentVariable,
        ];
        string[] missing = required
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        if (missing.Length > 0)
        {
            Skip = $"Set {string.Join(", ", missing)} to run SQL/Redis integration tests.";
        }
    }
}