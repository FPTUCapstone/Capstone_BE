using FluentAssertions;

using StackExchange.Redis;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Operator.CheckIn.Audit;
using TripMate.Infrastructure.Services.Audit;

namespace TripMate.Api.IntegrationTests.Tickets;

public sealed class CheckInAuditRedisJournalTests
{
    [RedisFact]
    public async Task Append_SameEventIsIdempotent_AndRequiresAofFsyncOnSameConnection()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        await using var multiplexer = await fixture.CreateIndependentMultiplexerAsync();
        var prefix = fixture.CreateUniquePrefix();
        var options = Options(prefix);
        await using var connection = new CheckInAuditRedisConnection(multiplexer, ownsMultiplexer: false);
        var journal = new RedisCheckInAuditJournal(connection, options);
        var eventId = Guid.NewGuid();
        var auditEvent = CheckInAuditEvent.Admission(
            eventId,
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-11-05T01:02:03Z"),
            41,
            51);

        var first = await journal.AppendAsync(auditEvent, CancellationToken.None);
        var replay = await journal.AppendAsync(auditEvent, CancellationToken.None);

        first.Should().Be(CheckInAuditWriteResult.Confirmed,
            "the isolated Redis must have AOF enabled and acknowledge local fsync");
        replay.Should().Be(CheckInAuditWriteResult.Confirmed);
        (await multiplexer.GetDatabase().StreamLengthAsync(options.AdmissionStreamKey))
            .Should().Be(1);

        var conflicting = CheckInAuditEvent.Admission(
            eventId,
            auditEvent.ScanAttemptId,
            auditEvent.OccurredAtUtc,
            auditEvent.ActorUserId,
            scheduleId: 52);
        (await journal.AppendAsync(conflicting, CancellationToken.None))
            .Should().Be(CheckInAuditWriteResult.Conflict);
    }

    [RedisFact]
    public async Task Append_AdmissionAndRejectionUseSeparateStreams()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        await using var multiplexer = await fixture.CreateIndependentMultiplexerAsync();
        var prefix = fixture.CreateUniquePrefix();
        var options = Options(prefix);
        await using var connection = new CheckInAuditRedisConnection(multiplexer, ownsMultiplexer: false);
        var journal = new RedisCheckInAuditJournal(connection, options);
        var attemptId = Guid.NewGuid();

        var admission = CheckInAuditEvent.Admission(
            Guid.NewGuid(), attemptId, DateTimeOffset.UtcNow, 41, 51);
        var rejected = CheckInAuditEvent.Rejected(
            Guid.NewGuid(), attemptId, DateTimeOffset.UtcNow, 41, 51, 61, "checkin.invalid_qr");

        (await journal.AppendAsync(admission, CancellationToken.None))
            .Should().Be(CheckInAuditWriteResult.Confirmed);
        (await journal.AppendAsync(rejected, CancellationToken.None))
            .Should().Be(CheckInAuditWriteResult.Confirmed);

        var db = multiplexer.GetDatabase();
        (await db.StreamLengthAsync(options.AdmissionStreamKey)).Should().Be(1);
        (await db.StreamLengthAsync(options.RejectionStreamKey)).Should().Be(1);
    }

    private static CheckInAuditJournalOptions Options(string prefix) => new()
    {
        Enabled = true,
        AdmissionStreamKey = $"{prefix}:admissions",
        RejectionStreamKey = $"{prefix}:rejections",
        ReceiptKeyPrefix = $"{prefix}:receipts",
        CommandTimeout = TimeSpan.FromSeconds(3),
        DurabilityTimeout = TimeSpan.FromSeconds(2),
        MaxAppendAttempts = 2,
    };
}