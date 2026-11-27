using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.Delete;
using TripMate.Application.Features.Operator.Tours.Departures.Add;
using TripMate.Application.Features.Operator.Tours.Get;
using TripMate.Application.Features.Operator.Tours.List;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Departures.Add;

public sealed class AddTourDepartureTests
{
    private static readonly DateTimeOffset BaseNow = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Validator_RejectsInvalidDatesPricesCapacitiesAndCoordinates()
    {
        var clock = new FakeDateTimeProvider { UtcNow = BaseNow };
        var validator = new AddTourDepartureCommandValidator(clock);
        var valid = ValidCommand(1, BaseNow);

        validator.Validate(valid).IsValid.Should().BeTrue();
        validator.Validate(valid with { StartAtUtc = BaseNow.AddMinutes(-5) }).IsValid.Should().BeFalse();
        validator.Validate(valid with { EndAtUtc = valid.StartAtUtc }).IsValid.Should().BeFalse();
        validator.Validate(valid with { MinimumCapacity = valid.TotalCapacity + 1 }).IsValid.Should().BeFalse();
        validator.Validate(valid with { RegistrationDeadlineAtUtc = BaseNow.AddMinutes(-1) }).IsValid.Should().BeFalse();
        validator.Validate(valid with { RegistrationDeadlineAtUtc = valid.StartAtUtc }).IsValid.Should().BeFalse();
        validator.Validate(valid with { AdultPrice = 0m }).IsValid.Should().BeFalse();
        validator.Validate(valid with { AdultPrice = 1000.5m }).IsValid.Should().BeFalse();
        validator.Validate(valid with { AdultPrice = 10_000_000_000m }).IsValid.Should().BeFalse();
        validator.Validate(valid with { ChildPrice = -1m }).IsValid.Should().BeFalse();
        validator.Validate(valid with { InfantPrice = 500.25m }).IsValid.Should().BeFalse();
        validator.Validate(valid with { MeetingLatitude = 16.0m, MeetingLongitude = null }).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ForPublishedTourWithCompletedPastDeparture_AddsNewDepartureAndPreservesHistory()
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = BaseNow };
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var tour = await SeedTourWithCompletedDepartureAsync(db, operatorUser.Id, TourStatus.Published, BaseNow);
        var handler = new AddTourDepartureCommandHandler(
            db, new FakeCurrentUserService { UserId = operatorUser.Id }, clock);

        var cmd = ValidCommand(tour.Id, BaseNow) with
        {
            AdultPrice = 1_800_000m,
            ChildPrice = 900_000m,
            InfantPrice = 0m,
            MinimumCapacity = 4,
            TotalCapacity = 18,
            MeetingPoint = "Cổng chính Công viên Biển Đông",
        };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsReplay.Should().BeFalse();
        result.Value.AdultPrice.Should().Be(1_800_000m);
        result.Value.ChildPrice.Should().Be(900_000m);
        result.Value.InfantPrice.Should().Be(0m);
        result.Value.MinimumCapacity.Should().Be(4);
        result.Value.TotalCapacity.Should().Be(18);
        result.Value.Status.Should().Be(nameof(TourScheduleStatus.Scheduled));

        var schedules = await db.TourSchedules.Where(s => s.TourId == tour.Id).OrderBy(s => s.StartAtUtc).ToListAsync();
        schedules.Should().HaveCount(2);
        schedules[0].Status.Should().Be(TourScheduleStatus.Completed);
        schedules[1].Id.Should().Be(result.Value.ScheduleId);
        schedules[1].Status.Should().Be(TourScheduleStatus.Scheduled);
    }

    [Fact]
    public async Task Handle_WhenReplayedWithSameIdempotencyKey_ReturnsExistingScheduleWithoutDuplicate()
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = BaseNow };
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var tour = await SeedTourWithCompletedDepartureAsync(db, operatorUser.Id, TourStatus.Published, BaseNow);
        var handler = new AddTourDepartureCommandHandler(
            db, new FakeCurrentUserService { UserId = operatorUser.Id }, clock);
        var cmd = ValidCommand(tour.Id, BaseNow);

        var first = await handler.Handle(cmd, CancellationToken.None);
        var replay = await handler.Handle(cmd, CancellationToken.None);
        var mismatch = await handler.Handle(cmd with { TotalCapacity = cmd.TotalCapacity + 2 }, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.IsReplay.Should().BeTrue();
        replay.Value.ScheduleId.Should().Be(first.Value.ScheduleId);
        mismatch.IsFailure.Should().BeTrue();
        mismatch.ErrorCode.Should().Be(OperatorTourErrorCodes.IdempotencyKeyPayloadMismatch);
        (await db.TourSchedules.CountAsync(s => s.TourId == tour.Id)).Should().Be(2);
    }

    [Fact]
    public async Task Handle_WhenDuplicateStartTimeExists_ReturnsDuplicateConflict()
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = BaseNow };
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var tour = await SeedTourWithCompletedDepartureAsync(db, operatorUser.Id, TourStatus.Published, BaseNow);
        var handler = new AddTourDepartureCommandHandler(
            db, new FakeCurrentUserService { UserId = operatorUser.Id }, clock);
        var first = await handler.Handle(ValidCommand(tour.Id, BaseNow), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var duplicate = await handler.Handle(ValidCommand(tour.Id, BaseNow) with { IdempotencyKey = Guid.NewGuid() }, CancellationToken.None);

        duplicate.IsFailure.Should().BeTrue();
        duplicate.ErrorCode.Should().Be(OperatorTourErrorCodes.DuplicateDeparture);
    }

    [Fact]
    public async Task Handle_WhenDepartureWindowDoesNotFitItinerary_Returns422Mismatch()
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = BaseNow };
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var tour = await SeedTourWithCompletedDepartureAsync(db, operatorUser.Id, TourStatus.Published, BaseNow);
        var handler = new AddTourDepartureCommandHandler(
            db, new FakeCurrentUserService { UserId = operatorUser.Id }, clock);

        // Stop on Day 1 is 08:00..09:30 ICT (01:00..02:30 UTC). Window starting at 04:00 UTC misses it.
        var badStart = new DateTimeOffset(BaseNow.AddDays(10).Date, TimeSpan.Zero).AddHours(4);
        var result = await handler.Handle(
            ValidCommand(tour.Id, BaseNow) with { StartAtUtc = badStart, EndAtUtc = badStart.AddHours(8) },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.DepartureItineraryMismatch);
    }

    [Theory]
    [InlineData(TourStatus.Draft)]
    [InlineData(TourStatus.Pending)]
    [InlineData(TourStatus.Rejected)]
    [InlineData(TourStatus.Inactive)]
    public async Task Handle_WhenTourIsNotApprovedAndPublished_ReturnsInvalidState(TourStatus status)
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = BaseNow };
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var tour = await SeedTourWithCompletedDepartureAsync(db, operatorUser.Id, status, BaseNow);
        var handler = new AddTourDepartureCommandHandler(
            db, new FakeCurrentUserService { UserId = operatorUser.Id }, clock);

        var result = await handler.Handle(ValidCommand(tour.Id, BaseNow), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Handle_ForForeignOperatorOrUnapprovedOperator_DeniesAccessWithoutDisclosure()
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = BaseNow };
        var owner = await SeedApprovedOperatorAsync(db, 7);
        var foreign = await SeedApprovedOperatorAsync(db, 8);
        var tour = await SeedTourWithCompletedDepartureAsync(db, owner.Id, TourStatus.Published, BaseNow);

        var foreignResult = await new AddTourDepartureCommandHandler(
            db, new FakeCurrentUserService { UserId = foreign.Id }, clock)
            .Handle(ValidCommand(tour.Id, BaseNow), CancellationToken.None);

        foreignResult.IsFailure.Should().BeTrue();
        foreignResult.ErrorCode.Should().Be(OperatorTourErrorCodes.TourNotFound);

        var unapprovedResult = await new AddTourDepartureCommandHandler(
            db, new FakeCurrentUserService { UserId = 999 }, clock)
            .Handle(ValidCommand(tour.Id, BaseNow), CancellationToken.None);

        unapprovedResult.IsFailure.Should().BeTrue();
        unapprovedResult.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
    }

    [Fact]
    public async Task ListGetAndDelete_ReportDepartureCountsPoiNameAndDeleteCreatedDraftSafely()
    {
        await using var db = TestDbContext.Create();
        var clock = new FakeDateTimeProvider { UtcNow = BaseNow };
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var tour = await SeedTourWithCompletedDepartureAsync(db, operatorUser.Id, TourStatus.Published, BaseNow);

        await new AddTourDepartureCommandHandler(db, new FakeCurrentUserService { UserId = operatorUser.Id }, clock)
            .Handle(ValidCommand(tour.Id, BaseNow), CancellationToken.None);

        var list = await new ListOperatorToursQueryHandler(db, new FakeCurrentUserService { UserId = operatorUser.Id }, clock)
            .Handle(new ListOperatorToursQuery(), CancellationToken.None);
        list.IsSuccess.Should().BeTrue();
        list.Value.Items.Single().UpcomingDepartureCount.Should().Be(1);
        list.Value.Items.Single().CompletedDepartureCount.Should().Be(1);
        list.Value.Items.Single().TotalDepartureCount.Should().Be(2);

        var detail = await new GetOperatorTourQueryHandler(db, new FakeCurrentUserService { UserId = operatorUser.Id }, clock)
            .Handle(new GetOperatorTourQuery(tour.Id), CancellationToken.None);
        detail.IsSuccess.Should().BeTrue();
        detail.Value.ItineraryItems.Single().PoiName.Should().Be("Bà Nà Hills");
        detail.Value.Departures.Count(d => d.IsCompleted).Should().Be(1);
        detail.Value.Departures.Count(d => !d.IsCompleted).Should().Be(1);

        // Also verify deleting a created Draft tour succeeds cleanly even when TourPackageCreateOperations references it
        var created = await new CreateTourPackageCommandHandler(
            db, new FakeCurrentUserService { UserId = operatorUser.Id }, clock)
            .Handle(new CreateTourPackageCommand(
                "Draft to delete", "Desc", null, 1_000_000m, 1,
                [TourAuthoringTestData.DestinationId], TourAuthoringTestData.PolicyTemplateId,
                null, null,
                [new CreateTourItineraryItemRequest(1, 1, 88, new TimeOnly(8, 0), 60, null)],
                [new CreateTourScheduleRequest(BaseNow.AddDays(5), BaseNow.AddDays(5).AddHours(10), "Center", 10)],
                Guid.NewGuid()), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var deleted = await new DeleteOperatorTourCommandHandler(
            db, new FakeCurrentUserService { UserId = operatorUser.Id }, clock)
            .Handle(new DeleteOperatorTourCommand(created.Value.TourId), CancellationToken.None);
        deleted.IsSuccess.Should().BeTrue();
        (await db.Tours.AnyAsync(t => t.Id == created.Value.TourId)).Should().BeFalse();
    }

    private static AddTourDepartureCommand ValidCommand(long tourId, DateTimeOffset now)
    {
        var dayStart = new DateTimeOffset(now.AddDays(14).Date, TimeSpan.FromHours(7)).ToUniversalTime();
        var dayEnd = new DateTimeOffset(now.AddDays(14).Date.AddHours(23).AddMinutes(59).AddSeconds(59), TimeSpan.FromHours(7)).ToUniversalTime();
        return new AddTourDepartureCommand(
            tourId,
            Guid.NewGuid(),
            dayStart,
            dayEnd,
            "Trung tâm Đà Nẵng",
            20,
            2,
            dayStart.AddDays(-2),
            1_500_000m,
            800_000m,
            0m);
    }

    private static async Task<User> SeedApprovedOperatorAsync(TestDbContext db, long id)
    {
        var user = new User { Id = id, Email = $"{id}@test.local", FullName = "Operator", Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = id,
            User = user,
            CompanyName = "Tour Co",
            TaxCode = $"Tax{id}",
            BusinessLicenseNo = $"Lic{id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        if (!await db.PointsOfInterest.AnyAsync(p => p.Id == 88L))
        {
            await TourAuthoringTestData.SeedReferenceDataAsync(db);
            var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
            typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, 88L);
            typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Name))!.SetValue(poi, "Bà Nà Hills");
            typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!.SetValue(poi, PointOfInterestStatus.Active);
            db.PointsOfInterest.Add(poi);
        }
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Tour> SeedTourWithCompletedDepartureAsync(
        TestDbContext db,
        long operatorId,
        TourStatus status,
        DateTimeOffset now)
    {
        var policy = await db.CancellationPolicyTemplates.Include(t => t.Rules).FirstAsync();
        var pastStart = now.AddDays(-10);
        var pastEnd = now.AddDays(-9);
        var pastSchedule = TourSchedule.Create(pastStart, pastEnd, "Old point", 10, now.AddDays(-20));
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Status))!.SetValue(pastSchedule, TourScheduleStatus.Completed);

        var tour = Tour.CreateDraft(
            operatorId,
            null,
            "Đà Nẵng - Hội An",
            "Hành trình di sản",
            1_200_000m,
            1,
            [TourAuthoringTestData.DestinationId],
            "Da Nang",
            policy,
            [TourItineraryItem.Create(1, 1, 88, new TimeOnly(8, 0), 90, "Visit")],
            [pastSchedule],
            now.AddDays(-25));

        if (status is TourStatus.Pending or TourStatus.Approved or TourStatus.Published or TourStatus.Inactive or TourStatus.Rejected)
        {
            tour.SubmitForReview(now.AddDays(-24));
        }
        if (status == TourStatus.Rejected)
        {
            tour.Reject(99, "Fix details", now.AddDays(-23));
        }
        else if (status == TourStatus.Approved)
        {
            tour.Approve(99, now.AddDays(-23), publishedAtUtc: null);
        }
        else if (status == TourStatus.Published)
        {
            tour.Approve(99, now.AddDays(-23), publishedAtUtc: now.AddDays(-23));
            tour.Publish(99, now.AddDays(-23));
        }
        else if (status == TourStatus.Inactive)
        {
            tour.Approve(99, now.AddDays(-23), publishedAtUtc: now.AddDays(-23));
            tour.Unpublish(now.AddDays(-22));
        }

        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        tour.SetVersionLineage(tour.Id, 1);
        await db.SaveChangesAsync();
        return tour;
    }
}