using FluentAssertions;

using StackExchange.Redis;

using TripMate.Infrastructure.Services.Audit;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class CheckInAuditProjectionWorkerTests
{
    [Fact]
    public void AcknowledgementZero_IsRecordedAsMissAndNotReportedAsProcessed()
    {
        var metrics = new CheckInAuditJournalMetrics();

        bool acknowledged = CheckInAuditProjectionBackgroundService.RecordAcknowledgement(0, metrics);

        acknowledged.Should().BeFalse();
        metrics.AcknowledgementMisses.Should().Be(1);
    }

    [Fact]
    public void AcknowledgementOne_IsAcceptedWithoutRecordingMiss()
    {
        var metrics = new CheckInAuditJournalMetrics();

        bool acknowledged = CheckInAuditProjectionBackgroundService.RecordAcknowledgement(1, metrics);

        acknowledged.Should().BeTrue();
        metrics.AcknowledgementMisses.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not-json")]
    [InlineData("[]")]
    [InlineData("\"scalar\"")]
    [InlineData("123")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1,\"eventId\":123}")]
    [InlineData("{\"schemaVersion\":1,\"eventId\":\"not-a-guid\",\"scanAttemptId\":\"00000000000000000000000000000001\",\"eventType\":\"Rejected\",\"occurredAtUtc\":\"2026-11-05T01:02:03Z\",\"actorUserId\":7,\"scheduleId\":9,\"ticketId\":11,\"reasonCode\":\"checkin.invalid_qr\"}")]
    [InlineData("{\"schemaVersion\":1,\"eventId\":\"00000000000000000000000000000001\",\"scanAttemptId\":\"00000000000000000000000000000002\",\"eventType\":\"Rejected\",\"occurredAtUtc\":\"2026-11-05T01:02:03Z\",\"actorUserId\":\"abc\",\"scheduleId\":9,\"ticketId\":11,\"reasonCode\":\"checkin.invalid_qr\"}")]
    [InlineData("{\"schemaVersion\":1,\"eventId\":\"00000000000000000000000000000001\",\"scanAttemptId\":\"00000000000000000000000000000002\",\"eventType\":\"Rejected\",\"occurredAtUtc\":\"2026-11-05T01:02:03Z\",\"actorUserId\":7,\"scheduleId\":9,\"ticketId\":\"bad\",\"reasonCode\":\"checkin.invalid_qr\"}")]
    [InlineData("{\"schemaVersion\":1,\"eventId\":\"00000000000000000000000000000001\",\"scanAttemptId\":\"00000000000000000000000000000002\",\"eventType\":\"Rejected\",\"occurredAtUtc\":\"2026-11-05T01:02:03Z\",\"actorUserId\":7,\"scheduleId\":9,\"ticketId\":null,\"reasonCode\":123}")]
    [InlineData("{\"schemaVersion\":1,\"eventId\":\"00000000000000000000000000000001\",\"scanAttemptId\":\"00000000000000000000000000000002\",\"eventType\":\"Rejected\",\"occurredAtUtc\":\"2026-11-05T01:02:03Z\",\"actorUserId\":7,\"scheduleId\":9,\"ticketId\":null,\"reasonCode\":\"\"}")]
    public void Parse_MalformedPayloads_ThrowHandledMalformedExceptions(string payload)
    {
        var entry = new StreamEntry("1-0", [new NameValueEntry("payload", payload)]);

        Action act = () => CheckInAuditProjectionBackgroundService.Parse(entry);

        Exception exception = act.Should().Throw<Exception>().Which;
        bool isHandledMalformedType = exception is System.Text.Json.JsonException
            or FormatException
            or InvalidDataException
            or ArgumentException;
        isHandledMalformedType.Should().BeTrue(
            "malformed stream entries must be isolated per entry, never throwing {0}",
            exception.GetType().Name);
    }
}