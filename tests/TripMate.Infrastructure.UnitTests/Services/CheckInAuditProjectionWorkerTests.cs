using FluentAssertions;

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
}