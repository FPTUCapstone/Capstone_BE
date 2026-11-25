using FluentAssertions;

using TripMate.Application.Features.Admin.Tours.Review.PendingChangesQueue;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.Tours.Review;

public sealed class GetModerationQueueQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WhenActorIsNotStaff_ReturnsAdminAccessRequired()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetModerationQueueQueryHandler(db, new FakeCurrentUserService
        {
            UserId = 1, Role = nameof(UserRole.TourOperator),
        });

        var result = await handler.Handle(new GetModerationQueueQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.admin_access_required");
    }

    [Fact]
    public async Task Handle_CombinesPendingToursAndPendingReviewChangeSets_EachTaggedWithItsKind()
    {
        await using var db = TestDbContext.Create();
        var staff = await SeedStaffAsync(db);
        var operatorUser = await SeedOperatorAsync(db);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, 88L);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!
            .SetValue(poi, PointOfInterestStatus.Active);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();

        var newSubmissionTour = NewTour(operatorUser.Id, "New submission tour");
        db.Tours.Add(newSubmissionTour);
        await db.SaveChangesAsync();
        newSubmissionTour.SubmitForReview(Now);
        await db.SaveChangesAsync();

        var approvedTour = NewTour(operatorUser.Id, "Approved tour");
        db.Tours.Add(approvedTour);
        await db.SaveChangesAsync();
        approvedTour.SubmitForReview(Now);
        approvedTour.Approve(staff.Id, Now);
        await db.SaveChangesAsync();
        var createHandler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = operatorUser.Id }, new FakeDateTimeProvider { UtcNow = Now });
        var created = await createHandler.Handle(new CreatePendingChangeCommand(approvedTour.Id), CancellationToken.None);
        var changeSet = await db.TourPendingChangeSets.FindAsync(created.Value.Id) ?? throw new InvalidOperationException();
        changeSet.Submit(Now.AddHours(1));
        await db.SaveChangesAsync();

        var handler = new GetModerationQueueQueryHandler(db, new FakeCurrentUserService
        {
            UserId = staff.Id, Role = nameof(UserRole.Staff),
        });

        var result = await handler.Handle(new GetModerationQueueQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Should().Contain(item =>
            item.Kind == ModerationQueueKinds.NewSubmission && item.Title == "New submission tour");
        result.Value.Items.Should().Contain(item =>
            item.Kind == ModerationQueueKinds.DraftVersion && item.TourId == approvedTour.Id);
    }

    private static Tour NewTour(long operatorUserId, string title) => Tour.CreateDraft(
        operatorUserId, null, title, "Description", 100_000m, 1,
        destinationIds: [TourAuthoringTestData.DestinationId], destinationSummary: "Da Nang",
        cancellationPolicy: TourAuthoringTestData.NewPolicy(),
        itineraryItems: [TourItineraryItem.Create(1, 1, 88, null, 60, null)],
        schedules: [TourSchedule.Create(Now.AddDays(5), Now.AddDays(6), null, 10, Now, adultPrice: 100_000m)],
        nowUtc: Now);

    private static async Task<User> SeedStaffAsync(TestDbContext db)
    {
        var user = new User
        {
            Id = 500, Email = "staff@test.local", FullName = "Staff",
            Role = UserRole.Staff, Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<User> SeedOperatorAsync(TestDbContext db)
    {
        var user = new User
        {
            Id = 1, Email = "1@test.local", FullName = "Operator",
            Role = UserRole.TourOperator, Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = 1, User = user, CompanyName = "Tour Co", TaxCode = "Tax1",
            BusinessLicenseNo = "Lic1", ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await db.SaveChangesAsync();
        return user;
    }
}
