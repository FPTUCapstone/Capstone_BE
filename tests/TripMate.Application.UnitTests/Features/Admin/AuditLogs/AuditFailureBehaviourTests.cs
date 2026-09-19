using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.AuditLogs.Common;
using TripMate.Application.Features.Admin.TourOperatorApplications.Approve;
using TripMate.Application.Features.Admin.TourOperatorApplications.Common;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Admin.AuditLogs;

public class AuditFailureBehaviourTests
{
    private readonly Mock<IAuditFailureRecorder> _recorder = new();
    private readonly FakeCurrentUserService _user = new() { UserId = 1, Role = "Administrator" };

    [Fact]
    public async Task StateFailure_IsRecordedAndOriginalResultPreserved()
    {
        var expected = Result.Failure<ApproveOperatorApplicationResponseDto>(TourOperatorApplicationErrorCodes.NotPending, "Already reviewed");
        var actual = await Behaviour().Handle(new(42), _ => Task.FromResult(expected), CancellationToken.None);
        actual.Should().BeSameAs(expected);
        _recorder.Verify(x => x.RecordAsync(It.Is<AuditFailureEvent>(e => e.ActorUserId == 1 && e.AffectedEntityId == 42
            && e.ErrorCode == TourOperatorApplicationErrorCodes.NotPending)), Times.Once);
    }

    [Theory]
    [InlineData("admin.tour_operator_application_forbidden")]
    [InlineData("admin.tour_operator_application_incomplete")]
    [InlineData("admin.tour_operator_application_document_invalid")]
    public async Task AuthorizationAndValidationFailure_DoNotCreateBusinessAudit(string code)
    {
        await Behaviour().Handle(new(42), _ => Task.FromResult(Result.Failure<ApproveOperatorApplicationResponseDto>(code, "Rejected")), CancellationToken.None);
        _recorder.Verify(x => x.RecordAsync(It.IsAny<AuditFailureEvent>()), Times.Never);
    }

    [Fact]
    public async Task Success_DoesNotDuplicateHandlerAudit()
    {
        await Behaviour().Handle(new(42), _ => Task.FromResult(Result.Success<ApproveOperatorApplicationResponseDto>(null!)), CancellationToken.None);
        _recorder.Verify(x => x.RecordAsync(It.IsAny<AuditFailureEvent>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmedDatabaseWriteFailure_IsRecordedAndExceptionPreserved()
    {
        var expected = new DbUpdateConcurrencyException("conflict");
        Func<Task> act = () => Behaviour().Handle(new(42), _ => throw expected, CancellationToken.None);
        (await act.Should().ThrowAsync<DbUpdateConcurrencyException>()).Which.Should().BeSameAs(expected);
        _recorder.Verify(x => x.RecordAsync(It.Is<AuditFailureEvent>(e => e.ErrorCode == "audit.concurrent_update")), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UncertainOutcome_DoesNotInventFailure(bool canceled)
    {
        Exception expected = canceled ? new OperationCanceledException() : new TimeoutException("commit outcome unknown");
        Func<Task> act = () => Behaviour().Handle(new(42), _ => throw expected, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
        _recorder.Verify(x => x.RecordAsync(It.IsAny<AuditFailureEvent>()), Times.Never);
    }

    private AuditFailureBehaviour<ApproveOperatorApplicationCommand, Result<ApproveOperatorApplicationResponseDto>> Behaviour() =>
        new(_recorder.Object, _user);
}