using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Admin.Tours.Review;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Tours.Shared;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.Tours.Review;

public sealed class UnpublishTourCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Domain_Unpublish_ApprovedWithActivePublishedAt_TransitionsToInactiveAndPreservesMetadata()
    {
        var tour = PublicTourTestData.NewTour(operatorId: 1);
        tour.SubmitForReview(Now.AddHours(-2));
        tour.SetVersionLineage(500, 2);
        var approvedAt = Now.AddHours(-1);
        tour.Approve(reviewerUserId: 10, reviewedAtUtc: approvedAt, publishedAtUtc: approvedAt);

        tour.CanUnpublish(Now).Should().BeTrue();
        tour.Unpublish(Now);

        tour.Status.Should().Be(TourStatus.Inactive);
        tour.ReviewedBy.Should().Be(10);
        tour.ReviewedAtUtc.Should().Be(approvedAt);
        tour.PublishedAtUtc.Should().Be(approvedAt);
        tour.VersionNo.Should().Be(2);
        tour.VersionRootTourId.Should().Be(500);
        tour.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Domain_Unpublish_PublishedTour_TransitionsToInactive()
    {
        var tour = PublicTourTestData.NewTour(operatorId: 1);
        tour.SubmitForReview(Now.AddHours(-3));
        tour.Approve(reviewerUserId: 10, reviewedAtUtc: Now.AddHours(-2));
        var publishedAt = Now.AddHours(-1);
        tour.Publish(reviewerUserId: 11, publishedAtUtc: publishedAt);

        tour.CanUnpublish(Now).Should().BeTrue();
        tour.Unpublish(Now);

        tour.Status.Should().Be(TourStatus.Inactive);
        tour.PublishedAtUtc.Should().Be(publishedAt);
        tour.ReviewedBy.Should().Be(10);
    }

    [Fact]
    public void Domain_Unpublish_ApprovedWithoutPublishedAt_ThrowsInvalidOperationException()
    {
        var tour = PublicTourTestData.NewTour(operatorId: 1);
        tour.SubmitForReview(Now.AddHours(-2));
        tour.Approve(reviewerUserId: 10, reviewedAtUtc: Now.AddHours(-1)); // PublishedAtUtc == null

        tour.CanUnpublish(Now).Should().BeFalse();
        var action = () => tour.Unpublish(Now);
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Only Published or publicly active Approved tours can be unpublished.");
    }

    [Fact]
    public void Domain_Unpublish_ApprovedWithFuturePublishedAt_ThrowsInvalidOperationException()
    {
        var tour = PublicTourTestData.NewTour(operatorId: 1);
        tour.SubmitForReview(Now.AddHours(-2));
        tour.Approve(reviewerUserId: 10, reviewedAtUtc: Now.AddHours(-1), publishedAtUtc: Now.AddHours(2));

        tour.CanUnpublish(Now).Should().BeFalse();
        var action = () => tour.Unpublish(Now);
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Only Published or publicly active Approved tours can be unpublished.");
    }

    [Theory]
    [InlineData(TourStatus.Draft)]
    [InlineData(TourStatus.Pending)]
    [InlineData(TourStatus.Rejected)]
    [InlineData(TourStatus.Inactive)]
    public void Domain_Unpublish_IneligibleStatuses_ThrowsInvalidOperationException(TourStatus status)
    {
        var tour = PublicTourTestData.NewTour(operatorId: 1);
        switch (status)
        {
            case TourStatus.Pending:
                tour.SubmitForReview(Now.AddHours(-2));
                break;
            case TourStatus.Rejected:
                tour.SubmitForReview(Now.AddHours(-2));
                tour.Reject(10, "Invalid", Now.AddHours(-1));
                break;
            case TourStatus.Inactive:
                tour.SubmitForReview(Now.AddHours(-3));
                tour.Approve(10, Now.AddHours(-2), publishedAtUtc: Now.AddHours(-2));
                tour.Unpublish(Now.AddHours(-1));
                break;
        }

        tour.CanUnpublish(Now).Should().BeFalse();
        var action = () => tour.Unpublish(Now);
        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(99L, nameof(UserRole.Administrator))]
    [InlineData(99L, nameof(UserRole.TourOperator))]
    [InlineData(99L, nameof(UserRole.Traveler))]
    public async Task Handle_WhenCallerNotStaff_ReturnsAdminAccessRequired(long? userId, string? role)
    {
        await using var db = TestDbContext.Create();
        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = userId, Role = role },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new UnpublishTourCommand(1), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AdminAccessRequired);
    }

    [Fact]
    public async Task Handle_WhenTourNotFound_ReturnsTourNotFound()
    {
        await using var db = TestDbContext.Create();
        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 50, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new UnpublishTourCommand(99999), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.TourNotFound);
    }

    [Theory]
    [InlineData(TourStatus.Draft)]
    [InlineData(TourStatus.Pending)]
    [InlineData(TourStatus.Rejected)]
    [InlineData(TourStatus.Inactive)]
    [InlineData(TourStatus.Approved)] // Approved with PublishedAtUtc == null
    public async Task Handle_WhenTourIneligible_ReturnsInvalidState(TourStatus status)
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, status);
        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 50, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new UnpublishTourCommand(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Handle_WhenApprovedWithFuturePublishedAt_ReturnsInvalidState()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddApprovedWithPublicationDateAsync(db, 1, Now.AddMinutes(30));
        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 50, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new UnpublishTourCommand(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Handle_WhenApprovedWithActivePublishedAt_UnpublishesRecordsApprovedBeforeStatusAndHidesTour()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var publishedAt = Now.AddMinutes(-15);
        var tour = await PublicTourTestData.AddApprovedWithPublicationDateAsync(db, 1, publishedAt);
        tour.SetVersionLineage(tour.Id, 1);
        await db.SaveChangesAsync();

        var visibleBefore = await db.Tours
            .Include(t => t.OperatorProfile).ThenInclude(p => p.User)
            .Where(TourVisibility.PublicTour(Now))
            .AnyAsync(t => t.Id == tour.Id);
        visibleBefore.Should().BeTrue();

        const long moderatorId = 77;
        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = moderatorId, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new UnpublishTourCommand(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(TourStatus.Inactive.ToString());
        result.Value.ChangedAtUtc.Should().Be(Now);

        var stored = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tour.Id);
        stored.Status.Should().Be(TourStatus.Inactive);
        stored.PublishedAtUtc.Should().Be(publishedAt);
        stored.ReviewedBy.Should().Be(2);

        var visibleAfter = await db.Tours
            .Include(t => t.OperatorProfile).ThenInclude(p => p.User)
            .Where(TourVisibility.PublicTour(Now))
            .AnyAsync(t => t.Id == tour.Id);
        visibleAfter.Should().BeFalse();

        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.AffectedEntityId == tour.Id && a.ActionType == AuditActionTypes.TourUnpublished);
        audit.ActorUserId.Should().Be(moderatorId);
        audit.Result.Should().Be(AuditOutcome.Success);

        using var beforeJson = JsonDocument.Parse(audit.BeforeData!);
        beforeJson.RootElement.GetProperty("Status").GetString().Should().Be(TourStatus.Approved.ToString());
        beforeJson.RootElement.GetProperty("PublishedAtUtc").GetDateTimeOffset().Should().Be(publishedAt);

        using var afterJson = JsonDocument.Parse(audit.AfterData!);
        afterJson.RootElement.GetProperty("Status").GetString().Should().Be(TourStatus.Inactive.ToString());
        afterJson.RootElement.GetProperty("ModeratedBy").GetInt64().Should().Be(moderatorId);
        afterJson.RootElement.GetProperty("VersionNo").GetInt32().Should().Be(1);
        afterJson.RootElement.GetProperty("VersionRootTourId").GetInt64().Should().Be(tour.Id);
        afterJson.RootElement.GetProperty("PublishedAtUtc").GetDateTimeOffset().Should().Be(publishedAt);
    }

    [Fact]
    public async Task Handle_WhenPublishedTour_UnpublishesAndRecordsPublishedBeforeStatus()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Published);

        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 88, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new UnpublishTourCommand(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(TourStatus.Inactive.ToString());

        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.AffectedEntityId == tour.Id && a.ActionType == AuditActionTypes.TourUnpublished);
        using var beforeJson = JsonDocument.Parse(audit.BeforeData!);
        beforeJson.RootElement.GetProperty("Status").GetString().Should().Be(TourStatus.Published.ToString());
    }

    [Fact]
    public async Task Handle_WhenTransientDeadlockOccurs_RetriesAndSucceeds()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddApprovedWithPublicationDateAsync(db, 1, Now.AddMinutes(-10));
        var initialSaves = db.SaveChangesAsyncCallCount;
        db.SaveChangesAttemptFailureFactory = attempt =>
            attempt == (initialSaves + 1) ? new SimulatedDeadlockException() : null;

        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 55, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new UnpublishTourCommand(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(TourStatus.Inactive.ToString());
        db.SaveChangesAsyncCallCount.Should().Be(initialSaves + 2);
    }

    [Fact]
    public async Task Handle_WhenDeadlockExhaustsRetries_ReturnsServiceUnavailableMsg127()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddApprovedWithPublicationDateAsync(db, 1, Now.AddMinutes(-10));
        db.SaveChangesFailureFactory = () => new SimulatedDeadlockException();

        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 55, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new UnpublishTourCommand(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.ServiceUnavailable);
        db.TransactionExecutionCount.Should().Be(3);

        var stored = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tour.Id);
        stored.Status.Should().Be(TourStatus.Approved);
    }

    [Fact]
    public async Task Handle_WhenUnrecoverableDbUpdateFailureOccurs_ReturnsMsg127WithoutRetrying()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddApprovedWithPublicationDateAsync(db, 1, Now.AddMinutes(-10));
        db.SaveChangesFailureFactory = () => new DbUpdateException("Simulated storage outage.");

        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 55, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new UnpublishTourCommand(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.ServiceUnavailable);
        db.TransactionExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenUnexpectedProgrammingErrorOccurs_DoesNotDisguiseAsMsg127()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddApprovedWithPublicationDateAsync(db, 1, Now.AddMinutes(-10));
        db.SaveChangesFailureFactory = () => new InvalidOperationException("Unexpected bug.");

        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 55, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var act = () => handler.Handle(new UnpublishTourCommand(tour.Id), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Unexpected bug.");
        db.TransactionExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenCancellationRequested_PropagatesOperationCanceledException()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddApprovedWithPublicationDateAsync(db, 1, Now.AddMinutes(-10));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new UnpublishTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 55, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var act = () => handler.Handle(new UnpublishTourCommand(tour.Id), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}