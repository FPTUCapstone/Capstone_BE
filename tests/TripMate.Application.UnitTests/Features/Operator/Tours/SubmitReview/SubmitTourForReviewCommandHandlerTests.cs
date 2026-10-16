using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.SubmitReview;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.SubmitReview;

public sealed class SubmitTourForReviewCommandHandlerTests
{
    [Fact]
    public async Task Handle_WhenDraftHasPrimaryMediaAndFutureDeparture_TransitionsToPending()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var create = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, clock);
        var created = await create.Handle(Command(88, clock.UtcNow), CancellationToken.None);
        var tour = await db.Tours.SingleAsync();
        db.TourMedia.Add(TripMate.Domain.Entities.TourMedia.Create(tour, "tour/cover", "https://cdn.example.test/cover.jpg",
            null, "Da Nang tour cover", 1, true, clock.UtcNow));
        await db.SaveChangesAsync();
        var handler = new SubmitTourForReviewCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, clock);

        var result = await handler.Handle(new SubmitTourForReviewCommand(created.Value.TourId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.Tours.SingleAsync()).Status.Should().Be(TourStatus.Pending);
        (await db.AuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenRejectedTourIsResubmitted_AuditRecordsRejectedAsTheBeforeStatus()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var user = new FakeCurrentUserService { UserId = actor.Id };
        var created = await new CreateTourPackageCommandHandler(db, user, clock)
            .Handle(Command(88, clock.UtcNow), CancellationToken.None);
        var tour = await db.Tours.SingleAsync();
        db.TourMedia.Add(TripMate.Domain.Entities.TourMedia.Create(tour, "tour/cover", "https://cdn.example.test/cover.jpg",
            null, "Da Nang tour cover", 1, true, clock.UtcNow));
        await db.SaveChangesAsync();
        var handler = new SubmitTourForReviewCommandHandler(db, user, clock);
        (await handler.Handle(new SubmitTourForReviewCommand(created.Value.TourId), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        tour.Reject(99, "Needs a clearer description.", clock.UtcNow);
        await db.SaveChangesAsync();

        var resubmitted = await handler.Handle(new SubmitTourForReviewCommand(created.Value.TourId), CancellationToken.None);

        resubmitted.IsSuccess.Should().BeTrue();
        var audits = await db.AuditLogs.OrderBy(log => log.Id).ToListAsync();
        audits.Should().HaveCount(2);
        audits[0].BeforeData.Should().Contain("Draft");
        audits[1].BeforeData.Should().Contain("Rejected").And.NotContain("Draft");
    }

    [Fact]
    public async Task Handle_WhenDraftHasNoPrimaryMedia_LeavesDraftUnchanged()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var create = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, clock);
        var created = await create.Handle(Command(88, clock.UtcNow), CancellationToken.None);
        var handler = new SubmitTourForReviewCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, clock);

        var result = await handler.Handle(new SubmitTourForReviewCommand(created.Value.TourId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        (await db.Tours.SingleAsync()).Status.Should().Be(TourStatus.Draft);
    }

    [Fact]
    public async Task Handle_WhenLegacyDraftHasNoDestination_LeavesDraftUnchanged()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var created = await new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, clock).Handle(Command(88, clock.UtcNow), CancellationToken.None);
        var tour = await db.Tours.SingleAsync();
        db.TourMedia.Add(TripMate.Domain.Entities.TourMedia.Create(tour, "tour/cover", "https://cdn.example.test/cover.jpg",
            null, "Da Nang tour cover", 1, true, clock.UtcNow));
        db.TourDestinations.RemoveRange(await db.TourDestinations.ToListAsync());
        await db.SaveChangesAsync();
        var handler = new SubmitTourForReviewCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, clock);

        var result = await handler.Handle(new SubmitTourForReviewCommand(created.Value.TourId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        (await db.Tours.SingleAsync()).Status.Should().Be(TourStatus.Draft);
    }

    private static CreateTourPackageCommand Command(long poiId, DateTimeOffset now) => new(
        "Da Nang day tour", "A full day visiting Da Nang landmarks.", null, 1_250_000m, 1,
        [TourAuthoringTestData.DestinationId], TourAuthoringTestData.PolicyTemplateId, null, null,
        [new CreateTourItineraryItemRequest(1, 1, poiId, new TimeOnly(8, 0), 90, null)],
        [new CreateTourScheduleRequest(now.AddDays(2), now.AddDays(2).AddHours(8), "City center", 20)],
        Guid.NewGuid());

    private static async Task<User> SeedApprovedOperatorAsync(TestDbContext db, long id)
    {
        var user = new User { Id = id, Email = $"{id}@test.local", FullName = "Operator", Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile { UserId = id, User = user, CompanyName = "Tour Co", TaxCode = "Tax7", BusinessLicenseNo = "Lic7", ApprovalStatus = OperatorApprovalStatus.Approved });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task SeedPoiAsync(TestDbContext db, long id)
    {
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, id);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!.SetValue(poi, PointOfInterestStatus.Active);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
    }
}