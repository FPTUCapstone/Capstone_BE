using FluentAssertions;

using TripMate.Application.Features.Operator.CheckIn.Audit;
using TripMate.Infrastructure.Services.Audit;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class RedisCheckInAuditJournalEvidenceTests
{
    [Fact]
    public void Evaluate_SamePhysicalConnectionAndLocalFsync_Confirms()
    {
        var result = CheckInAuditBatchEvidence.Evaluate(
            connectionIdBefore: 42,
            connectionIdAfter: 42,
            CheckInAuditAppendDisposition.Created,
            localFsyncCount: 1);

        result.Should().Be(CheckInAuditWriteResult.Confirmed);
    }

    [Fact]
    public void Evaluate_ReconnectedBetweenAppendAndWait_IsUncertain()
    {
        var result = CheckInAuditBatchEvidence.Evaluate(
            connectionIdBefore: 42,
            connectionIdAfter: 43,
            CheckInAuditAppendDisposition.Created,
            localFsyncCount: 1);

        result.Should().Be(CheckInAuditWriteResult.Uncertain);
    }

    [Fact]
    public void Evaluate_NoLocalAofFsync_IsUncertain()
    {
        var result = CheckInAuditBatchEvidence.Evaluate(
            connectionIdBefore: 42,
            connectionIdAfter: 42,
            CheckInAuditAppendDisposition.Existing,
            localFsyncCount: 0);

        result.Should().Be(CheckInAuditWriteResult.Uncertain);
    }

    [Fact]
    public void Evaluate_ConflictingDigest_IsConflictEvenWhenAofSynced()
    {
        var result = CheckInAuditBatchEvidence.Evaluate(
            connectionIdBefore: 42,
            connectionIdAfter: 42,
            CheckInAuditAppendDisposition.Conflict,
            localFsyncCount: 1);

        result.Should().Be(CheckInAuditWriteResult.Conflict);
    }
}