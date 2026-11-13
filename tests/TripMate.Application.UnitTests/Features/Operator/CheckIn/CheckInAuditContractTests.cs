using FluentAssertions;

using TripMate.Application.Features.Operator.CheckIn.Audit;

namespace TripMate.Application.UnitTests.Features.Operator.CheckIn;

public sealed class CheckInAuditContractTests
{
    [Fact]
    public void Admission_DoesNotCarryRejectedReasonOrSensitivePayload()
    {
        var attemptId = Guid.NewGuid();
        var admission = CheckInAuditEvent.Admission(
            Guid.NewGuid(), attemptId, DateTimeOffset.UtcNow, 42, 123);

        admission.EventType.Should().Be(CheckInAuditEventType.Admission);
        admission.ScanAttemptId.Should().Be(attemptId);
        admission.ReasonCode.Should().BeNull();
        admission.TicketId.Should().BeNull();
    }

    [Fact]
    public void Rejection_UsesSeparateEventIdentityAndStableReasonCode()
    {
        var attemptId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var rejection = CheckInAuditEvent.Rejected(
            eventId, attemptId, DateTimeOffset.UtcNow, 42, 123, 456, "checkin.invalid_qr");

        rejection.EventId.Should().Be(eventId);
        rejection.ScanAttemptId.Should().Be(attemptId);
        rejection.EventType.Should().Be(CheckInAuditEventType.Rejected);
        rejection.ReasonCode.Should().Be("checkin.invalid_qr");
    }

    [Fact]
    public void RejectionEventId_IsDeterministicPerAttemptAndSeparateFromAttemptId()
    {
        var attemptId = Guid.NewGuid();

        Guid first = CheckInAuditEvent.DeriveRejectionEventId(attemptId);
        Guid retry = CheckInAuditEvent.DeriveRejectionEventId(attemptId);

        first.Should().Be(retry);
        first.Should().NotBe(Guid.Empty);
        first.Should().NotBe(attemptId);
        CheckInAuditEvent.DeriveRejectionEventId(Guid.NewGuid()).Should().NotBe(first);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Event_RejectsInvalidActor(long actorUserId)
    {
        var act = () => CheckInAuditEvent.Admission(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, actorUserId, 123);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}