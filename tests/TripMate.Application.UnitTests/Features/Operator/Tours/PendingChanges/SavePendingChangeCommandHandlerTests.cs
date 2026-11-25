using FluentAssertions;

using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Create;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Save;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.PendingChanges;

public sealed class SavePendingChangeCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WhenChangeSetIsPendingReview_ReturnsInvalidState()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var changeSet = await CreateDraftAsync(db, actor, tour);
        changeSet.Submit(Now);
        await db.SaveChangesAsync();
        var handler = new SavePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(
            BuildCommand(tour.Id, changeSet.Id, [new PendingChangeScheduleItem(
                null, Now.AddDays(10), Now.AddDays(11), null, 10)]),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.invalid_state");
    }

    [Theory]
    [InlineData(TourPendingChangeStatus.Draft)]
    [InlineData(TourPendingChangeStatus.Rejected)]
    public async Task Handle_WhenChangeSetIsDraftOrRejected_SavesTheProposedFields(
        TourPendingChangeStatus status)
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var changeSet = await CreateDraftAsync(db, actor, tour);
        if (status == TourPendingChangeStatus.Rejected)
        {
            changeSet.Submit(Now);
            changeSet.Reject(99, "Fix pricing", Now);
            await db.SaveChangesAsync();
        }
        var handler = new SavePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now.AddHours(1) });

        var result = await handler.Handle(
            BuildCommand(tour.Id, changeSet.Id, [new PendingChangeScheduleItem(
                null, Now.AddDays(10), Now.AddDays(11), null, 10)], title: "Saved title"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await db.TourPendingChangeSets.FindAsync(changeSet.Id);
        reloaded!.Title.Should().Be("Saved title");
        reloaded.UpdatedAtUtc.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public async Task Handle_WhenProposedScheduleCapacityIsBelowSoldSlots_ReturnsScheduleHasActiveBookings()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var liveSchedule = tour.Schedules.Single();
        liveSchedule.ReserveCapacity(5);
        await db.SaveChangesAsync();
        var changeSet = await CreateDraftAsync(db, actor, tour);
        var handler = new SavePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now.AddHours(1) });

        var result = await handler.Handle(
            BuildCommand(tour.Id, changeSet.Id, [new PendingChangeScheduleItem(
                liveSchedule.Id, Now.AddDays(20), Now.AddDays(21), null, 4)]),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.schedule_has_active_bookings");
    }

    [Fact]
    public async Task Handle_WhenProposedScheduleCapacityEqualsSoldSlots_Succeeds()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var liveSchedule = tour.Schedules.Single();
        liveSchedule.ReserveCapacity(5);
        await db.SaveChangesAsync();
        var changeSet = await CreateDraftAsync(db, actor, tour);
        var handler = new SavePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now.AddHours(1) });

        var result = await handler.Handle(
            BuildCommand(tour.Id, changeSet.Id, [new PendingChangeScheduleItem(
                liveSchedule.Id, Now.AddDays(20), Now.AddDays(21), null, 5)]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenScheduleIdBelongsToAnotherTour_ReturnsScheduleNotOwnedByTour()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var (_, otherTour) = await SeedApprovedTourAsync(db, operatorId: 200, categoryKey: 2);
        var foreignScheduleId = otherTour.Schedules.Single().Id;
        var changeSet = await CreateDraftAsync(db, actor, tour);
        var handler = new SavePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now.AddHours(1) });

        var result = await handler.Handle(
            BuildCommand(tour.Id, changeSet.Id, [new PendingChangeScheduleItem(
                foreignScheduleId, Now.AddDays(20), Now.AddDays(21), null, 10)]),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.schedule_not_owned_by_tour");
    }

    [Fact]
    public async Task Handle_WhenAPastDepartureScheduleIsInThePayload_IsNeverRejectedForBr22()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db, scheduleStartOffset: TimeSpan.FromDays(-10));
        var pastSchedule = tour.Schedules.Single();
        var changeSet = await CreateDraftAsync(db, actor, tour);
        var handler = new SavePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now.AddHours(1) });

        var result = await handler.Handle(
            BuildCommand(tour.Id, changeSet.Id, [new PendingChangeScheduleItem(
                pastSchedule.Id, pastSchedule.StartAtUtc, pastSchedule.EndAtUtc, pastSchedule.MeetingPoint,
                pastSchedule.TotalCapacity, AdultPrice: pastSchedule.AdultPrice)]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenANewOrFutureEditedScheduleHasAnInvalidDate_ReturnsValidationFailure()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var changeSet = await CreateDraftAsync(db, actor, tour);
        var handler = new SavePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now.AddHours(1) });

        var result = await handler.Handle(
            BuildCommand(tour.Id, changeSet.Id, [new PendingChangeScheduleItem(
                null, Now.AddHours(1).AddDays(-1), Now.AddHours(2), null, 10)]),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.invalid_schedule_date");
    }

    private static SavePendingChangeCommand BuildCommand(
        long tourId, long changeSetId, IReadOnlyList<PendingChangeScheduleItem> schedules,
        string title = "Title") =>
        new(tourId, changeSetId, 1, title, "Description", 200_000m, 1,
            [TourAuthoringTestData.DestinationId], TourAuthoringTestData.PolicyTemplateId, null, null,
            [new CreateTourItineraryItemRequest(1, 1, 88, null, 60, null)], schedules);

    private static async Task<(User Actor, Tour Tour)> SeedApprovedTourAsync(
        TestDbContext db, long operatorId = 1, int categoryKey = 1,
        TimeSpan? scheduleStartOffset = null)
    {
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        var user = new User
        {
            Id = operatorId,
            Email = $"{operatorId}@test.local",
            FullName = "Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorId,
            User = user,
            CompanyName = "Tour Co",
            TaxCode = $"Tax{operatorId}",
            BusinessLicenseNo = $"Lic{operatorId}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        var poiId = 80 + categoryKey;
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, (long)poiId);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!
            .SetValue(poi, PointOfInterestStatus.Active);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();

        var start = Now.Add(scheduleStartOffset ?? TimeSpan.FromDays(5));
        var tour = Tour.CreateDraft(
            operatorId, null, "Seed tour", "Seed description", 100_000m, 1,
            destinationIds: [TourAuthoringTestData.DestinationId], destinationSummary: "Da Nang",
            cancellationPolicy: TourAuthoringTestData.NewPolicy(),
            itineraryItems: [TourItineraryItem.Create(1, 1, poiId, null, 60, null)],
            schedules: [TourSchedule.Create(start, start.AddDays(1), null, 10, Now, adultPrice: 100_000m)],
            nowUtc: Now);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        return (user, tour);
    }

    private static async Task<TourPendingChangeSet> CreateDraftAsync(TestDbContext db, User actor, Tour tour)
    {
        tour.SubmitForReview(Now);
        tour.Approve(99, Now);
        await db.SaveChangesAsync();
        var createHandler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });
        var created = await createHandler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);
        return await db.TourPendingChangeSets.FindAsync(created.Value.Id) ?? throw new InvalidOperationException();
    }
}