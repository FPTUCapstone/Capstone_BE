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

public sealed class ApproveTourCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private static TourReviewChecklistDto ValidChecklist() => new(
        ContentCompleteness: true,
        ImageAppropriateness: true,
        PricePlausibility: true,
        PolicyCompliance: true,
        ItineraryFeasibility: true);

    [Fact]
    public async Task Handle_WhenCallerNotStaff_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 99, Role = nameof(UserRole.Administrator) },
            clock);

        var result = await handler.Handle(
            new ApproveTourCommand(100, ValidChecklist()),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AdminAccessRequired);
    }

    [Fact]
    public async Task Handle_WhenCallerUnauthenticated_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = null, Role = null },
            clock);

        var result = await handler.Handle(
            new ApproveTourCommand(100, ValidChecklist()),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AdminAccessRequired);
    }

    [Fact]
    public async Task Handle_WhenChecklistNullOrIncomplete_ReturnsReviewChecklistIncomplete()
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 99, Role = nameof(UserRole.Staff) },
            clock);

        var incompleteChecklist = new TourReviewChecklistDto(
            ContentCompleteness: true,
            ImageAppropriateness: false,
            PricePlausibility: true,
            PolicyCompliance: true,
            ItineraryFeasibility: true);

        var result = await handler.Handle(
            new ApproveTourCommand(100, incompleteChecklist),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.ReviewChecklistIncomplete);
    }

    [Fact]
    public async Task Handle_WhenTourNotFound_ReturnsNotFound()
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 99, Role = nameof(UserRole.Staff) },
            clock);

        var result = await handler.Handle(
            new ApproveTourCommand(999999, ValidChecklist()),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.TourNotFound);
    }

    [Theory]
    [InlineData(TourStatus.Draft)]
    [InlineData(TourStatus.Rejected)]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Published)]
    public async Task Handle_WhenTourNotPending_ReturnsInvalidState(TourStatus nonPendingStatus)
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, nonPendingStatus);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 99, Role = nameof(UserRole.Staff) },
            clock);

        var result = await handler.Handle(
            new ApproveTourCommand(tour.Id, ValidChecklist()),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.InvalidState);
        result.ErrorMessage.Should().Be("The selected package is no longer in the Pending Approval status.");
    }

    [Fact]
    public async Task Handle_WhenPendingTourAndChecklistPassed_TransitionsToApprovedAndSetsPublishedAt()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var staffId = 55L;
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = staffId, Role = nameof(UserRole.Staff) },
            clock);

        var result = await handler.Handle(
            new ApproveTourCommand(tour.Id, ValidChecklist(), "Verified all requirements."),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");
        result.Value.TourId.Should().Be(tour.Id);
        result.Value.Status.Should().Be(TourStatus.Approved.ToString());
        result.Value.ChangedAtUtc.Should().Be(Now);

        var updatedTour = await db.Tours.SingleAsync(t => t.Id == tour.Id);
        updatedTour.Status.Should().Be(TourStatus.Approved);
        updatedTour.ReviewedBy.Should().Be(staffId);
        updatedTour.ReviewedAtUtc.Should().Be(Now);
        updatedTour.PublishedAtUtc.Should().Be(Now);
        updatedTour.RejectionReason.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenApproved_PersistsRichAuditSnapshot()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var staffId = 77L;
        var note = "Internal note: high quality imagery and valid pricing.";
        var checklist = ValidChecklist();
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = staffId, Role = nameof(UserRole.Staff) },
            clock);

        var result = await handler.Handle(
            new ApproveTourCommand(tour.Id, checklist, note),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");

        var audit = await db.AuditLogs.SingleAsync(a => a.AffectedEntityId == tour.Id && a.ActionType == AuditActionTypes.TourApproved);
        audit.ActorUserId.Should().Be(staffId);
        audit.AffectedEntity.Should().Be(AuditEntityTypes.Tour);
        audit.Result.Should().Be(AuditOutcome.Success);
        audit.CreatedAtUtc.Should().Be(Now);

        var afterJson = JsonDocument.Parse(audit.AfterData!).RootElement;
        afterJson.GetProperty("Status").GetString().Should().Be(TourStatus.Approved.ToString());
        afterJson.GetProperty("VersionNo").GetInt32().Should().Be(tour.VersionNo);
        afterJson.GetProperty("ReviewedBy").GetInt64().Should().Be(staffId);
        afterJson.GetProperty("ReviewedAtUtc").GetDateTimeOffset().Should().Be(Now);
        afterJson.GetProperty("PublishedAtUtc").GetDateTimeOffset().Should().Be(Now);
        afterJson.GetProperty("InternalNote").GetString().Should().Be(note);

        var checklistJson = afterJson.GetProperty("Checklist");
        checklistJson.GetProperty("ContentCompleteness").GetBoolean().Should().BeTrue();
        checklistJson.GetProperty("ImageAppropriateness").GetBoolean().Should().BeTrue();
        checklistJson.GetProperty("PricePlausibility").GetBoolean().Should().BeTrue();
        checklistJson.GetProperty("PolicyCompliance").GetBoolean().Should().BeTrue();
        checklistJson.GetProperty("ItineraryFeasibility").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenApproved_ActivatesPublicTourVisibility()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1, approval: OperatorApprovalStatus.Approved, accountStatus: AccountStatus.Active);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var staffId = 88L;
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = staffId, Role = nameof(UserRole.Staff) },
            clock);

        // Before approval, tour is NOT visible to public
        var visibleBefore = await db.Tours
            .Include(t => t.OperatorProfile)
                .ThenInclude(p => p.User)
            .Where(TourVisibility.PublicTour(Now))
            .AnyAsync(t => t.Id == tour.Id);
        visibleBefore.Should().BeFalse();

        // Approve tour
        var result = await handler.Handle(
            new ApproveTourCommand(tour.Id, ValidChecklist()),
            CancellationToken.None);
        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");

        // After approval, tour IS immediately visible to public under TourVisibility.PublicTour
        var visibleAfter = await db.Tours
            .Include(t => t.OperatorProfile)
                .ThenInclude(p => p.User)
            .Where(TourVisibility.PublicTour(Now))
            .AnyAsync(t => t.Id == tour.Id);
        visibleAfter.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenTransientDeadlockOccurs_RetriesAndSucceedsOnNextAttempt()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var staffId = 55L;

        var initialSaves = db.SaveChangesAsyncCallCount;

        db.SaveChangesAttemptFailureFactory = attempt =>
            attempt == (initialSaves + 1) ? new SimulatedDeadlockException() : null;

        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = staffId, Role = nameof(UserRole.Staff) },
            clock);

        var result = await handler.Handle(
            new ApproveTourCommand(tour.Id, ValidChecklist()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");
        result.Value.Status.Should().Be(TourStatus.Approved.ToString());

        var updatedTour = await db.Tours.SingleAsync(t => t.Id == tour.Id);
        updatedTour.Status.Should().Be(TourStatus.Approved);
        db.SaveChangesAsyncCallCount.Should().Be(initialSaves + 2);
    }

    [Fact]
    public async Task Handle_WhenDeadlockOccursAndTourDecidedConcurrently_RetriesAndReturnsInvalidStateMsg133()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var staffId = 55L;

        // On attempt 1, simulated deadlock happens while tour state reflects the concurrent winner's commit
        db.SerializableTransactionCompletionFailureFactory = attempt =>
            attempt == 1 ? new SimulatedDeadlockException() : null;

        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = staffId, Role = nameof(UserRole.Staff) },
            clock);

        var result = await handler.Handle(
            new ApproveTourCommand(tour.Id, ValidChecklist()),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.InvalidState);
        result.ErrorMessage.Should().Be("The selected package is no longer in the Pending Approval status.");
        db.TransactionExecutionCount.Should().Be(2);
    }

    [Fact]
    public async Task Handle_WhenDeadlockExhaustsRetries_ReturnsServiceUnavailableMsg127()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var staffId = 55L;

        // All attempts fail with deadlock before persisting
        db.SaveChangesFailureFactory = () => new SimulatedDeadlockException();

        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = staffId, Role = nameof(UserRole.Staff) },
            clock);

        var result = await handler.Handle(
            new ApproveTourCommand(tour.Id, ValidChecklist()),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.ServiceUnavailable);
        result.ErrorMessage.Should().Be("TripMate is temporarily unable to process your request. Please check your connection and try again.");
        db.TransactionExecutionCount.Should().Be(3);

        // Tour stays Pending and no audit record was persisted
        var storedTour = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tour.Id);
        storedTour.Status.Should().Be(TourStatus.Pending);
        var audits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.AffectedEntityId == tour.Id && a.ActionType == AuditActionTypes.TourApproved)
            .ToListAsync();
        audits.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenUnrecoverableSaveFailureOccurs_ReturnsServiceUnavailableMsg127_WithoutRetrying()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var staffId = 55L;

        db.SaveChangesFailureFactory = () => new DbUpdateException("Simulated storage outage.");

        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = staffId, Role = nameof(UserRole.Staff) },
            clock);

        var result = await handler.Handle(
            new ApproveTourCommand(tour.Id, ValidChecklist()),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.ServiceUnavailable);
        result.ErrorMessage.Should().Be("TripMate is temporarily unable to process your request. Please check your connection and try again.");
        // Non-deadlock infrastructure failure must NOT retry and must NOT be converted to 409 InvalidState
        db.TransactionExecutionCount.Should().Be(1);

        var storedTour = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tour.Id);
        storedTour.Status.Should().Be(TourStatus.Pending);
        var audits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.AffectedEntityId == tour.Id && a.ActionType == AuditActionTypes.TourApproved)
            .ToListAsync();
        audits.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenCancellationRequested_PropagatesOperationCanceledException()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var staffId = 55L;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = staffId, Role = nameof(UserRole.Staff) },
            clock);

        var act = () => handler.Handle(
            new ApproveTourCommand(tour.Id, ValidChecklist()),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Handle_WhenUnexpectedProgrammingErrorOccurs_DoesNotDisguiseAsMsg127()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var staffId = 55L;

        // Unexpected programming error (not DbUpdateException / DbException / TimeoutException / deadlock)
        db.SaveChangesFailureFactory = () => new InvalidOperationException("Unexpected programming invariant failure.");

        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = staffId, Role = nameof(UserRole.Staff) },
            clock);

        var act = () => handler.Handle(
            new ApproveTourCommand(tour.Id, ValidChecklist()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unexpected programming invariant failure.");
        db.TransactionExecutionCount.Should().Be(1);
    }

    // UC-60 PC-03: the Tour Operator is notified of the approval. In-app only for now (email delivery is a
    // separate outbox concern). The delivery intent commits with the decision.
    [Fact]
    public async Task Handle_WhenApproved_QueuesOneInAppNoticeForTheOwningOperator()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 55, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(
            new ApproveTourCommand(tour.Id, ValidChecklist(), "Internal moderator-only remark"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");
        var notices = await db.TourApprovalNotices.AsNoTracking().ToListAsync();
        var notice = notices.Should().ContainSingle().Subject;
        notice.OperatorUserId.Should().Be(tour.OperatorUserId);
        notice.TourId.Should().Be(tour.Id);
        notice.TourTitle.Should().Be(tour.Title);
        notice.ApprovedAtUtc.Should().Be(Now);
        notice.NextAttemptAtUtc.Should().Be(Now);
        (await db.Notifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WhenPersistenceFails_DoesNotLeaveANotificationBehind()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
        db.SaveChangesFailureFactory = () => new DbUpdateException("Simulated storage outage.");
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 55, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new ApproveTourCommand(tour.Id, ValidChecklist()), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorTourErrorCodes.ServiceUnavailable);
        (await db.TourApprovalNotices.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Rejected)]
    [InlineData(TourStatus.Draft)]
    public async Task Handle_WhenTourIsNotPending_CreatesNoNotification(TourStatus status)
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, status);
        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 55, Role = nameof(UserRole.Staff) },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new ApproveTourCommand(tour.Id, ValidChecklist()), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorTourErrorCodes.InvalidState);
        (await db.TourApprovalNotices.AnyAsync()).Should().BeFalse();
    }

    // [DH-04b] Supersede prior live version when v2 is approved

    [Theory]
    [InlineData(TourStatus.Published)]
    [InlineData(TourStatus.Approved)]
    public async Task Handle_WhenV2Approved_SupersedesLivePriorVersion(TourStatus v1Status)
    {
        // Arrange
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var now = Now;
        var clock = new FakeDateTimeProvider { UtcNow = now };
        var staffId = 55L;

        // Create v1 tour in live status
        var v1 = await PublicTourTestData.AddTourAsync(db, 1, v1Status);
        // Set PublishedAtUtc so CanUnpublish returns true for the Approved case
        typeof(Tour).GetProperty(nameof(Tour.PublishedAtUtc))!.SetValue(v1, (DateTimeOffset?)now.AddMinutes(-10));
        await db.SaveChangesAsync();

        // Create v2 tour (a Pending draft revision of v1)
        var v2 = PublicTourTestData.NewTour(1, "v2 of Hoi An food tour");
        v2.SetVersionLineage(v1.Id, 2);
        v2.SubmitForReview(now.AddDays(-1));
        db.Tours.Add(v2);
        await db.SaveChangesAsync();

        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = staffId, Role = nameof(UserRole.Staff) },
            clock);

        // Act
        var result = await handler.Handle(new ApproveTourCommand(v2.Id, ValidChecklist()), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");

        var updatedV1 = await db.Tours.SingleAsync(t => t.Id == v1.Id);
        updatedV1.Status.Should().Be(TourStatus.Inactive, "approving v2 must supersede v1 to Inactive");

        var updatedV2 = await db.Tours.SingleAsync(t => t.Id == v2.Id);
        updatedV2.Status.Should().Be(TourStatus.Approved, "v2 must be Approved");

        // Supersede audit log recorded
        var supersededAudit = await db.AuditLogs.SingleAsync(a =>
            a.AffectedEntityId == v1.Id && a.ActionType == AuditActionTypes.TourUnpublished);
        supersededAudit.ActorUserId.Should().Be(staffId);
        supersededAudit.Result.Should().Be(AuditOutcome.Success);
        var afterJson = JsonDocument.Parse(supersededAudit.AfterData!).RootElement;
        afterJson.GetProperty("SupersededByTourId").GetInt64().Should().Be(v2.Id);
        afterJson.GetProperty("Status").GetString().Should().Be(TourStatus.Inactive.ToString());
    }

    [Fact]
    public async Task Handle_WhenV2Approved_PreservesV1Schedules()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var now = Now;
        var clock = new FakeDateTimeProvider { UtcNow = now };

        var v1 = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Published);
        var v1ScheduleCount = await db.TourSchedules.CountAsync(s => s.TourId == v1.Id);

        var v2 = PublicTourTestData.NewTour(1, "v2 edition");
        v2.SetVersionLineage(v1.Id, 2);
        v2.SubmitForReview(now.AddDays(-1));
        db.Tours.Add(v2);
        await db.SaveChangesAsync();

        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 55, Role = nameof(UserRole.Staff) },
            clock);

        var result = await handler.Handle(new ApproveTourCommand(v2.Id, ValidChecklist()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");

        // v1 schedules preserved — only status changes, schedules untouched
        var v1ScheduleCountAfter = await db.TourSchedules.CountAsync(s => s.TourId == v1.Id);
        v1ScheduleCountAfter.Should().Be(v1ScheduleCount, "superseding must not touch v1 schedules");
    }

    [Fact]
    public async Task Handle_WhenV2ApprovedButV1NotInLiveState_DoesNotChangeV1()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var now = Now;
        var clock = new FakeDateTimeProvider { UtcNow = now };

        // v1 in Inactive status (not Published/Approved live) — not supersede-eligible
        var v1 = await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Inactive);

        var v2 = PublicTourTestData.NewTour(1, "v2 edition");
        v2.SetVersionLineage(v1.Id, 2);
        v2.SubmitForReview(now.AddDays(-1));
        db.Tours.Add(v2);
        await db.SaveChangesAsync();

        var handler = new ApproveTourCommandHandler(
            db,
            new FakeCurrentUserService { UserId = 55, Role = nameof(UserRole.Staff) },
            clock);

        var result = await handler.Handle(new ApproveTourCommand(v2.Id, ValidChecklist()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");

        // v1 must remain Inactive (was not in Published/Approved live state)
        var updatedV1 = await db.Tours.SingleAsync(t => t.Id == v1.Id);
        updatedV1.Status.Should().Be(TourStatus.Inactive);

        // No TourUnpublished audit added for v1
        var unpublishAudits = await db.AuditLogs
            .Where(a => a.AffectedEntityId == v1.Id && a.ActionType == AuditActionTypes.TourUnpublished)
            .ToListAsync();
        unpublishAudits.Should().BeEmpty();
    }
}