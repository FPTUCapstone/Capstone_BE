using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.Update;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Update;

public sealed class UpdateTourDraftCommandHandlerTests
{
    [Fact]
    public async Task Handle_ForOwnedDraft_ReplacesItineraryAndDeparture()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        await SeedPoiAsync(db, 89);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var create = new CreateTourPackageCommandHandler(db, new FakeCurrentUserService { UserId = actor.Id }, clock);
        var created = await create.Handle(Create(88, clock.UtcNow), CancellationToken.None);
        var handler = new UpdateTourDraftCommandHandler(db, new FakeCurrentUserService { UserId = actor.Id }, clock);

        var result = await handler.Handle(new UpdateTourDraftCommand(created.Value.TourId,
            "Updated tour", "Updated description", null, 2_000_000m, 1,
            [TourAuthoringTestData.SecondDestinationId], TourAuthoringTestData.PolicyTemplateId, " Meals ", null,
            [new CreateTourItineraryItemRequest(1, 1, 89, new TimeOnly(9, 0), 120, "Updated")],
            [new CreateTourScheduleRequest(clock.UtcNow.AddDays(4), clock.UtcNow.AddDays(4).AddHours(8), "Airport", 30)]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var tour = await db.Tours.Include(item => item.ItineraryItems).Include(item => item.Schedules).SingleAsync();
        tour.Title.Should().Be("Updated tour");
        tour.Inclusions.Should().Be("Meals");
        tour.DestinationSummary.Should().Be("Hoi An");
        (await db.TourDestinations.Where(link => link.TourId == tour.Id).Select(link => link.DestinationId)
            .ToListAsync()).Should().Equal(TourAuthoringTestData.SecondDestinationId);
        (await db.TourCancellationPolicyRules.CountAsync(rule => rule.TourId == tour.Id)).Should().Be(3);
        tour.ItineraryItems.Should().ContainSingle(item => item.PoiId == 89);
        tour.Schedules.Should().ContainSingle(item => item.TotalCapacity == 30);
    }

    [Fact]
    public async Task Handle_ForPendingTour_RejectsEdit()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var create = new CreateTourPackageCommandHandler(db, new FakeCurrentUserService { UserId = actor.Id }, clock);
        var created = await create.Handle(Create(88, clock.UtcNow), CancellationToken.None);
        var tour = await db.Tours.SingleAsync();
        tour.SubmitForReview(clock.UtcNow);
        await db.SaveChangesAsync();
        var handler = new UpdateTourDraftCommandHandler(db, new FakeCurrentUserService { UserId = actor.Id }, clock);

        var result = await handler.Handle(ToUpdate(created.Value.TourId, 88, clock.UtcNow), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    private static CreateTourPackageCommand Create(long poiId, DateTimeOffset now) => new("Original", "Original description", null,
        1_000_000m, 1, [TourAuthoringTestData.DestinationId], TourAuthoringTestData.PolicyTemplateId, null, null,
        [new CreateTourItineraryItemRequest(1, 1, poiId, null, 60, null)],
        [new CreateTourScheduleRequest(now.AddDays(2), now.AddDays(2).AddHours(8), null, 20)], Guid.NewGuid());
    private static UpdateTourDraftCommand ToUpdate(long id, long poiId, DateTimeOffset now) => new(id, "Updated", "Updated description", null,
        2_000_000m, 1, [TourAuthoringTestData.DestinationId], TourAuthoringTestData.PolicyTemplateId, null, null,
        [new CreateTourItineraryItemRequest(1, 1, poiId, null, 60, null)],
        [new CreateTourScheduleRequest(now.AddDays(3), now.AddDays(3).AddHours(8), null, 20)]);
    private static async Task<User> SeedApprovedOperatorAsync(TestDbContext db, long id)
    {
        var user = new User { Id = id, Email = $"{id}@test.local", FullName = "Operator", Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user); db.OperatorProfiles.Add(new OperatorProfile { UserId = id, User = user, CompanyName = "Tour Co", TaxCode = $"Tax{id}", BusinessLicenseNo = $"Lic{id}", ApprovalStatus = OperatorApprovalStatus.Approved });
        await db.SaveChangesAsync(); return user;
    }
    private static async Task SeedPoiAsync(TestDbContext db, long id)
    {
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, id);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!.SetValue(poi, PointOfInterestStatus.Active);
        db.PointsOfInterest.Add(poi); await db.SaveChangesAsync();
    }
}