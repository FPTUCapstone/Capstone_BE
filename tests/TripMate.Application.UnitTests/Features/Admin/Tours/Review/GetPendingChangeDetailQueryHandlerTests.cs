using FluentAssertions;

using TripMate.Application.Features.Admin.Tours.Review.PendingChangesQueue;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.Tours.Review;

public sealed class GetPendingChangeDetailQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WhenActorIsNotStaff_ReturnsAdminAccessRequired()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPendingChangeDetailQueryHandler(db, new FakeCurrentUserService
        {
            UserId = 1,
            Role = nameof(UserRole.TourOperator),
        });

        var result = await handler.Handle(new GetPendingChangeDetailQuery(1), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.admin_access_required");
    }

    [Fact]
    public async Task Handle_WhenPendingChangeNotFound_ReturnsPendingChangeNotFound()
    {
        await using var db = TestDbContext.Create();
        var staff = await SeedStaffAsync(db);
        var handler = new GetPendingChangeDetailQueryHandler(db, new FakeCurrentUserService
        {
            UserId = staff.Id,
            Role = nameof(UserRole.Staff),
        });

        var result = await handler.Handle(new GetPendingChangeDetailQuery(999), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.pending_change_not_found");
    }

    [Fact]
    public async Task Handle_ReturnsTheProposedValues_NotTheLiveTours()
    {
        await using var db = TestDbContext.Create();
        var staff = await SeedStaffAsync(db);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        var operatorUser = await SeedOperatorAsync(db);
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, 88L);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!
            .SetValue(poi, PointOfInterestStatus.Active);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();

        var tour = Tour.CreateDraft(
            operatorUser.Id, null, "Live title", "Live description", 1_000_000m, 1,
            destinationIds: [TourAuthoringTestData.DestinationId], destinationSummary: "Da Nang",
            cancellationPolicy: TourAuthoringTestData.NewPolicy(),
            itineraryItems: [TourItineraryItem.Create(1, 1, 88, null, 60, null)],
            schedules: [TourSchedule.Create(Now.AddDays(5), Now.AddDays(6), null, 12, Now, adultPrice: 1_000_000m)],
            nowUtc: Now);
        db.Tours.Add(tour);
        tour.SubmitForReview(Now);
        tour.Approve(99, Now);
        await db.SaveChangesAsync();

        var createHandler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = operatorUser.Id }, new FakeDateTimeProvider { UtcNow = Now });
        var created = await createHandler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);
        var changeSet = await db.TourPendingChangeSets.FindAsync(created.Value.Id) ?? throw new InvalidOperationException();
        changeSet.SaveDraft(
            changeSet.CategoryId, "Proposed title", "Proposed description", 1_200_000m, changeSet.DurationDays,
            changeSet.Inclusions, changeSet.Exclusions, changeSet.Highlights, changeSet.TravellerRequirements,
            changeSet.TimeZoneId, changeSet.DestinationIdsJson, changeSet.DestinationSummary,
            changeSet.CancellationPolicyTemplateId, changeSet.ItineraryItemsJson, changeSet.SchedulesJson,
            Now.AddHours(1));
        changeSet.Submit(Now.AddHours(2));
        await db.SaveChangesAsync();

        var handler = new GetPendingChangeDetailQueryHandler(db, new FakeCurrentUserService
        {
            UserId = staff.Id,
            Role = nameof(UserRole.Staff),
        });

        var result = await handler.Handle(new GetPendingChangeDetailQuery(changeSet.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Proposed title");
        result.Value.BasePrice.Should().Be(1_200_000m);
    }

    private static async Task<User> SeedStaffAsync(TestDbContext db)
    {
        var user = new User
        {
            Id = 500,
            Email = "staff@test.local",
            FullName = "Staff",
            Role = UserRole.Staff,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<User> SeedOperatorAsync(TestDbContext db)
    {
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
        await db.SaveChangesAsync();
        return user;
    }
}