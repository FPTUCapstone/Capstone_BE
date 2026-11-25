using FluentAssertions;

using TripMate.Application.Features.Operator.Tours.PendingChanges.Create;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Get;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.PendingChanges;

public sealed class GetCurrentPendingChangeQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WhenNoOpenDraftExists_ReturnsPendingChangeNotFound()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var handler = new GetCurrentPendingChangeQueryHandler(db, new FakeCurrentUserService { UserId = actor.Id });

        var result = await handler.Handle(new GetCurrentPendingChangeQuery(tour.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.pending_change_not_found");
    }

    [Fact]
    public async Task Handle_WhenAnOpenDraftExists_ReturnsIt()
    {
        await using var db = TestDbContext.Create();
        var (actor, tour) = await SeedApprovedTourAsync(db);
        var createHandler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });
        var created = await createHandler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);
        var handler = new GetCurrentPendingChangeQueryHandler(db, new FakeCurrentUserService { UserId = actor.Id });

        var result = await handler.Handle(new GetCurrentPendingChangeQuery(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(created.Value.Id);
        result.Value.Title.Should().Be(tour.Title);
        result.Value.Schedules.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_WhenTourNotOwnedByActor_ReturnsTourNotFound()
    {
        await using var db = TestDbContext.Create();
        var (_, tour) = await SeedApprovedTourAsync(db);
        var handler = new GetCurrentPendingChangeQueryHandler(db, new FakeCurrentUserService { UserId = 999 });

        var result = await handler.Handle(new GetCurrentPendingChangeQuery(tour.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.not_found");
    }

    private static async Task<(User Actor, Tour Tour)> SeedApprovedTourAsync(TestDbContext db)
    {
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
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
}
