using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Features.Operator.Tours.PendingChanges.Approve;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.PendingChanges;

public sealed class ApprovePendingChangeCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WhenActorIsNotStaff_ReturnsAdminAccessRequired()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var changeSet = await CreateSubmittedDraftAsync(db, actor, tour);
        var handler = BuildHandler(db, role: nameof(UserRole.TourOperator));

        var result = await handler.Handle(new ApprovePendingChangeCommand(changeSet.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.admin_access_required");
    }

    [Fact]
    public async Task Handle_WhenChangeSetNotFound_ReturnsPendingChangeNotFound()
    {
        await using var db = TestDbContext.Create();
        var handler = BuildHandler(db);

        var result = await handler.Handle(new ApprovePendingChangeCommand(999), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.pending_change_not_found");
    }

    [Fact]
    public async Task Handle_WhenNotPendingReview_ReturnsInvalidStateWithMsg133()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var createHandler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });
        var created = await createHandler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);
        var handler = BuildHandler(db);

        var result = await handler.Handle(new ApprovePendingChangeCommand(created.Value.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.invalid_state");
        result.ErrorMessage.Should().Contain("MSG133");
    }

    [Fact]
    public async Task Handle_WhenProposedCapacityIsBelowSoldSlots_ReturnsScheduleHasActiveBookings()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var liveSchedule = tour.Schedules.Single();
        var changeSet = await CreateSubmittedDraftAsync(db, actor, tour);
        liveSchedule.ReserveCapacity(8);
        var loweredSchedule = new PendingChangeScheduleItem(
            liveSchedule.Id, liveSchedule.StartAtUtc, liveSchedule.EndAtUtc, liveSchedule.MeetingPoint,
            TotalCapacity: 5, AdultPrice: liveSchedule.AdultPrice);
        typeof(TourPendingChangeSet).GetProperty(nameof(TourPendingChangeSet.SchedulesJson))!
            .SetValue(changeSet, PendingChangePayload.SerializeSchedules([loweredSchedule]));
        await db.SaveChangesAsync();
        var handler = BuildHandler(db);

        var result = await handler.Handle(new ApprovePendingChangeCommand(changeSet.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.schedule_has_active_bookings");
    }

    [Theory]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Published)]
    public async Task Handle_OnSuccess_AppliesChangesAndLeavesTourStatusUnchanged(TourStatus status)
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db, publish: status == TourStatus.Published);
        var changeSet = await CreateSubmittedDraftAsync(db, actor, tour, title: "Approved title");
        var handler = BuildHandler(db);

        var result = await handler.Handle(new ApprovePendingChangeCommand(changeSet.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TourStatus.Should().Be(status.ToString());
        var reloadedTour = await db.Tours.FindAsync(tour.Id);
        reloadedTour!.Status.Should().Be(status);
        reloadedTour.Title.Should().Be("Approved title");
        var reloadedChangeSet = await db.TourPendingChangeSets.FindAsync(changeSet.Id);
        reloadedChangeSet!.Status.Should().Be(TourPendingChangeStatus.Applied);
    }

    [Fact]
    public async Task Handle_OnSuccess_SendsOneInAppNotificationToTheSubmittingOperator()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var changeSet = await CreateSubmittedDraftAsync(db, actor, tour);
        var handler = BuildHandler(db);

        await handler.Handle(new ApprovePendingChangeCommand(changeSet.Id), CancellationToken.None);

        db.Notifications.Should().ContainSingle(notification =>
            notification.UserId == actor.Id
            && notification.Type == Notification.TourPendingChangeApprovedType);
    }

    [Fact]
    public async Task Handle_WhenTheNotificationStepFails_StillReturnsSuccess_AndDetachesTheFailedEntity()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var changeSet = await CreateSubmittedDraftAsync(db, actor, tour);
        var handler = BuildHandler(db);
        int callsBeforeHandle = db.SaveChangesAsyncCallCount;
        // Inside the transaction: Phase 1 (delete old children) + Phase 2 (apply + audit) = 2
        // saves; the notification attempt after commit is the 3rd save from this point.
        db.SaveChangesAttemptFailureFactory = attempt => attempt == callsBeforeHandle + 3
            ? new InvalidOperationException("Simulated notification save failure.")
            : null;

        var result = await handler.Handle(new ApprovePendingChangeCommand(changeSet.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the decision already committed before the notification step failed");
        db.Notifications.Should().BeEmpty("the failed Notification row was never committed");

        db.SaveChangesAttemptFailureFactory = null;
        var furtherSave = async () => await db.SaveChangesAsync(CancellationToken.None);
        await furtherSave.Should().NotThrowAsync(
            "the failed Notification entity must be detached, not retried by a later unrelated save");
    }

    private static ApprovePendingChangeCommandHandler BuildHandler(
        TestDbContext db, string role = nameof(UserRole.Staff), long actingUserId = 500) =>
        new(db, new FakeCurrentUserService { UserId = actingUserId, Role = role },
            new FakeDateTimeProvider { UtcNow = Now.AddHours(3) }, NullLogger<ApprovePendingChangeCommandHandler>.Instance);

    private static async Task<(User Actor, Tour Tour)> SeedApprovedTourAsync(TestDbContext db, bool publish = false)
    {
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        var user = new User
        {
            Id = 1,
            Email = "1@test.local",
            FullName = "Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = 1,
            User = user,
            CompanyName = "Tour Co",
            TaxCode = "Tax1",
            BusinessLicenseNo = "Lic1",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, 88L);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!
            .SetValue(poi, PointOfInterestStatus.Active);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();

        var tour = Tour.CreateDraft(
            1, null, "Seed tour", "Seed description", 100_000m, 1,
            destinationIds: [TourAuthoringTestData.DestinationId], destinationSummary: "Da Nang",
            cancellationPolicy: TourAuthoringTestData.NewPolicy(),
            itineraryItems: [TourItineraryItem.Create(1, 1, 88, null, 60, null)],
            schedules: [TourSchedule.Create(Now.AddDays(5), Now.AddDays(6), null, 10, Now, adultPrice: 100_000m)],
            nowUtc: Now);
        db.Tours.Add(tour);
        tour.SubmitForReview(Now);
        tour.Approve(99, Now);
        if (publish)
        {
            tour.Publish(99, Now);
        }
        await db.SaveChangesAsync();
        return (user, tour);
    }

    private static async Task<TourPendingChangeSet> CreateSubmittedDraftAsync(
        TestDbContext db, User actor, Tour tour, string? title = null)
    {
        var createHandler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });
        var created = await createHandler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);
        var changeSet = await db.TourPendingChangeSets.FindAsync(created.Value.Id) ?? throw new InvalidOperationException();
        if (title is not null)
        {
            changeSet.SaveDraft(
                changeSet.CategoryId, title, changeSet.Description, changeSet.BasePrice, changeSet.DurationDays,
                changeSet.Inclusions, changeSet.Exclusions, changeSet.Highlights, changeSet.TravellerRequirements,
                changeSet.TimeZoneId, changeSet.DestinationIdsJson, changeSet.DestinationSummary,
                changeSet.CancellationPolicyTemplateId, changeSet.ItineraryItemsJson, changeSet.SchedulesJson,
                Now.AddHours(1));
        }
        changeSet.Submit(Now.AddHours(2));
        await db.SaveChangesAsync();
        return changeSet;
    }
}