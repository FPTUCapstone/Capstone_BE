using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Features.Operator.Tours.PendingChanges.Create;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Reject;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.PendingChanges;

public sealed class RejectPendingChangeCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WhenActorIsNotStaff_ReturnsAdminAccessRequired()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var changeSet = await CreateSubmittedDraftAsync(db, actor, tour);
        var handler = BuildHandler(db, role: nameof(UserRole.TourOperator));

        var result = await handler.Handle(
            new RejectPendingChangeCommand(changeSet.Id, "A valid reason"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.admin_access_required");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Handle_WhenReasonIsEmpty_ReturnsRejectionReasonInvalidWithMsg123(string? reason)
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var changeSet = await CreateSubmittedDraftAsync(db, actor, tour);
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            new RejectPendingChangeCommand(changeSet.Id, reason!), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.pending_change_rejection_reason_invalid");
        result.ErrorMessage.Should().Contain("MSG123");
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

        var result = await handler.Handle(
            new RejectPendingChangeCommand(created.Value.Id, "A valid reason"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.invalid_state");
        result.ErrorMessage.Should().Contain("MSG133");
    }

    [Fact]
    public async Task Handle_OnSuccess_RejectsWritesOneAuditRow_AndNeverTouchesTheLiveTour()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var changeSet = await CreateSubmittedDraftAsync(db, actor, tour, title: "Rejected title");
        var tourTitleBefore = tour.Title;
        var tourStatusBefore = tour.Status;
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            new RejectPendingChangeCommand(changeSet.Id, "The pricing is inconsistent."), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Rejected");
        var reloadedChangeSet = await db.TourPendingChangeSets.FindAsync(changeSet.Id);
        reloadedChangeSet!.Status.Should().Be(TourPendingChangeStatus.Rejected);
        reloadedChangeSet.RejectionReason.Should().Be("The pricing is inconsistent.");
        var reloadedTour = await db.Tours.FindAsync(tour.Id);
        reloadedTour!.Title.Should().Be(tourTitleBefore, "rejecting a draft version never touches the live tour");
        reloadedTour.Status.Should().Be(tourStatusBefore);
        db.AuditLogs.Should().ContainSingle(log => log.ActionType == "TOUR_PENDING_CHANGE_REJECTED");
    }

    [Fact]
    public async Task Handle_OnSuccess_SendsOneInAppNotificationToTheSubmittingOperator()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var changeSet = await CreateSubmittedDraftAsync(db, actor, tour);
        var handler = BuildHandler(db);

        await handler.Handle(new RejectPendingChangeCommand(changeSet.Id, "A valid reason"), CancellationToken.None);

        db.Notifications.Should().ContainSingle(notification =>
            notification.UserId == actor.Id
            && notification.Type == Notification.TourPendingChangeRejectedType);
    }

    private static RejectPendingChangeCommandHandler BuildHandler(
        TestDbContext db, string role = nameof(UserRole.Staff), long actingUserId = 500) =>
        new(db, new FakeCurrentUserService { UserId = actingUserId, Role = role },
            new FakeDateTimeProvider { UtcNow = Now.AddHours(3) }, NullLogger<RejectPendingChangeCommandHandler>.Instance);

    private static async Task<(User Actor, Tour Tour)> SeedApprovedTourAsync(TestDbContext db)
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