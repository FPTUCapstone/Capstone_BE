using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.PendingChanges;

public sealed class CreatePendingChangeCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WhenActorIsNotAnEligibleOperator_ReturnsAccessRequired()
    {
        await using var db = TestDbContext.Create();
        var handler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = null }, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new CreatePendingChangeCommand(1), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.access_required");
    }

    [Fact]
    public async Task Handle_WhenTourNotFoundOrNotOwned_ReturnsTourNotFound()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedOperatorAsync(db, 1);
        var handler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new CreatePendingChangeCommand(999), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.not_found");
    }

    [Theory]
    [InlineData(TourStatus.Draft)]
    [InlineData(TourStatus.Rejected)]
    public async Task Handle_WhenTourIsDraftOrRejected_ReturnsInvalidState(TourStatus status)
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedOperatorAsync(db, 1);
        var tour = await SeedTourAsync(db, actor.Id);
        if (status == TourStatus.Rejected)
        {
            tour.SubmitForReview(Now);
            tour.Reject(99, "Needs changes", Now);
            await db.SaveChangesAsync();
        }
        var handler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.invalid_state");
    }

    [Fact]
    public async Task Handle_WhenTourIsPending_ReturnsInvalidStateWithMsg122()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedOperatorAsync(db, 1);
        var tour = await SeedTourAsync(db, actor.Id);
        tour.SubmitForReview(Now);
        await db.SaveChangesAsync();
        var handler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.invalid_state");
        result.ErrorMessage.Should().Contain("MSG122");
    }

    [Fact]
    public async Task Handle_WhenTourIsInactive_ReturnsInvalidState()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedOperatorAsync(db, 1);
        var tour = await SeedTourAsync(db, actor.Id);
        tour.SubmitForReview(Now);
        tour.Approve(99, Now);
        tour.Publish(99, Now);
        tour.Unpublish(Now);
        await db.SaveChangesAsync();
        var handler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.invalid_state");
    }

    [Theory]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Published)]
    public async Task Handle_WhenTourIsApprovedOrPublished_CreatesADraftSeededFromTheLiveTour(TourStatus status)
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedOperatorAsync(db, 1);
        var tour = await SeedTourAsync(db, actor.Id);
        tour.SubmitForReview(Now);
        tour.Approve(99, Now);
        if (status == TourStatus.Published)
        {
            tour.Publish(99, Now);
        }
        await db.SaveChangesAsync();
        var handler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsNew.Should().BeTrue();
        result.Value.Status.Should().Be("Draft");
        var changeSet = await db.TourPendingChangeSets.SingleAsync();
        changeSet.Title.Should().Be(tour.Title);
        changeSet.TourId.Should().Be(tour.Id);
    }

    [Fact]
    public async Task Handle_CalledTwiceWhileOneIsOpen_ReturnsTheSameExistingRow()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedOperatorAsync(db, 1);
        var tour = await SeedTourAsync(db, actor.Id);
        tour.SubmitForReview(Now);
        tour.Approve(99, Now);
        await db.SaveChangesAsync();
        var handler = new CreatePendingChangeCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider { UtcNow = Now });

        var first = await handler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);
        var second = await handler.Handle(new CreatePendingChangeCommand(tour.Id), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.Id.Should().Be(first.Value.Id);
        second.Value.IsNew.Should().BeFalse();
        (await db.TourPendingChangeSets.CountAsync()).Should().Be(1);
    }

    private static async Task<User> SeedOperatorAsync(TestDbContext db, long id)
    {
        var user = new User
        {
            Id = id, Email = $"{id}@test.local", FullName = "Operator",
            Role = UserRole.TourOperator, Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = id, User = user, CompanyName = "Tour Co", TaxCode = $"Tax{id}",
            BusinessLicenseNo = $"Lic{id}", ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Tour> SeedTourAsync(TestDbContext db, long operatorId)
    {
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, 88L);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!
            .SetValue(poi, PointOfInterestStatus.Active);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();

        var policy = TourAuthoringTestData.NewPolicy();
        var tour = Tour.CreateDraft(
            operatorId, null, "Seed tour", "Seed description", 100_000m, 1,
            destinationIds: [TourAuthoringTestData.DestinationId], destinationSummary: "Da Nang",
            cancellationPolicy: policy,
            itineraryItems: [TourItineraryItem.Create(1, 1, 88, null, 60, null)],
            schedules: [TourSchedule.Create(Now.AddDays(5), Now.AddDays(6), null, 10, Now, adultPrice: 100_000m)],
            nowUtc: Now);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        return tour;
    }
}
