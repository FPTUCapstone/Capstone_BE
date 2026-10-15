using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.AuditLogs.Common;
using TripMate.Application.Features.Admin.TourOperatorApplications.Approve;
using TripMate.Application.Features.Admin.TourOperatorApplications.Common;
using TripMate.Application.Features.Admin.TourOperatorApplications.Reject;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.PointsOfInterest.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;

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

    [Fact]
    public async Task RejectStateFailure_IsRecordedWithRejectAction()
    {
        var expected = Result.Failure<RejectOperatorApplicationResponseDto>(
            TourOperatorApplicationErrorCodes.NotPending,
            "Already reviewed");
        var behaviour = new AuditFailureBehaviour<RejectOperatorApplicationCommand,
            Result<RejectOperatorApplicationResponseDto>>(_recorder.Object, _user);

        var actual = await behaviour.Handle(
            new RejectOperatorApplicationCommand(42, "Invalid licence"),
            _ => Task.FromResult(expected),
            CancellationToken.None);

        actual.Should().BeSameAs(expected);
        _recorder.Verify(x => x.RecordAsync(It.Is<AuditFailureEvent>(e =>
            e.ActorUserId == 1 &&
            e.ActionType == TripMate.Domain.Common.AuditActionTypes.OperatorApplicationReject &&
            e.AffectedEntityId == 42 &&
            e.ErrorCode == TourOperatorApplicationErrorCodes.NotPending)), Times.Once);
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

    [Fact]
    public async Task PoiCreateStateFailure_ByStaff_IsRecordedWithPoiCreateAction()
    {
        _user.Role = "Staff";
        var expected = Result.Failure<PoiResponseDto>(PoiErrorCodes.ReferenceNotFound, "Missing category");

        var actual = await PoiBehaviour().Handle(
            new CreatePoiCommand("Museum", 1, null, null),
            _ => Task.FromResult(expected),
            CancellationToken.None);

        actual.Should().BeSameAs(expected);
        _recorder.Verify(x => x.RecordAsync(It.Is<AuditFailureEvent>(e =>
            e.ActorUserId == 1 &&
            e.ActionType == AuditActionTypes.PoiCreate &&
            e.ErrorCode == PoiErrorCodes.ReferenceNotFound)), Times.Once);
    }

    [Fact]
    public async Task PoiCreateStateFailure_ByAdministrator_IsNotRecorded()
    {
        // Creating a POI is a Staff action; an Administrator is rejected before any audited work.
        _user.Role = "Administrator";
        var failure = Result.Failure<PoiResponseDto>(PoiErrorCodes.ReferenceNotFound, "Missing category");

        await PoiBehaviour().Handle(
            new CreatePoiCommand("Museum", 1, null, null),
            _ => Task.FromResult(failure),
            CancellationToken.None);

        _recorder.Verify(x => x.RecordAsync(It.IsAny<AuditFailureEvent>()), Times.Never);
    }

    [Fact]
    public async Task OperatorApplicationStateFailure_ByStaff_IsNotRecorded()
    {
        // Reviewing operator applications stays an Administrator action.
        _user.Role = "Staff";
        var failure = Result.Failure<ApproveOperatorApplicationResponseDto>(TourOperatorApplicationErrorCodes.NotPending, "Already reviewed");

        await Behaviour().Handle(new(42), _ => Task.FromResult(failure), CancellationToken.None);

        _recorder.Verify(x => x.RecordAsync(It.IsAny<AuditFailureEvent>()), Times.Never);
    }

    private AuditFailureBehaviour<CreatePoiCommand, Result<PoiResponseDto>> PoiBehaviour() =>
        new(_recorder.Object, _user);

    private AuditFailureBehaviour<ApproveOperatorApplicationCommand, Result<ApproveOperatorApplicationResponseDto>> Behaviour() =>
        new(_recorder.Object, _user);
}