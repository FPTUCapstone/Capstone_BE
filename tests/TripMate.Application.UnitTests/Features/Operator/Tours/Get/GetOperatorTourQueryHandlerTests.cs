using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.Get;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Get;

public sealed class GetOperatorTourQueryHandlerTests
{
    [Fact]
    public async Task Handle_ForOwner_ReturnsDraftWithOrderedItineraryAndDepartures()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var create = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, clock);
        var created = await create.Handle(Command(88, clock.UtcNow), CancellationToken.None);
        var handler = new GetOperatorTourQueryHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, clock);

        var result = await handler.Handle(new GetOperatorTourQuery(created.Value.TourId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(created.Value.TourId);
        result.Value.Status.Should().Be("Draft");
        result.Value.ItineraryItems.Should().ContainSingle(item => item.PoiId == 88);
        result.Value.Departures.Should().ContainSingle();
        result.Value.Destinations.Select(destination => destination.DestinationId)
            .Should().Equal(TourAuthoringTestData.DestinationId, TourAuthoringTestData.SecondDestinationId);
        result.Value.Destinations.Select(destination => destination.Name).Should().Equal("Da Nang", "Hoi An");
        result.Value.Destination.Should().Be("Da Nang, Hoi An");
        result.Value.Inclusions.Should().Be("Hotel");
        result.Value.Exclusions.Should().BeNull();
        result.Value.CancellationPolicyTemplateId.Should().Be(TourAuthoringTestData.PolicyTemplateId);
        result.Value.CancellationPolicyRules.Select(rule => rule.DaysBeforeDeparture)
            .Should().Equal(30, 15, 0);
        result.Value.RejectionReason.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ForDraftWithoutImage_ReportsTheBlockerAndCannotSubmit()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var user = new FakeCurrentUserService { UserId = actor.Id };
        var created = await new CreateTourPackageCommandHandler(db, user, clock)
            .Handle(Command(88, clock.UtcNow), CancellationToken.None);

        var result = await new GetOperatorTourQueryHandler(db, user, clock)
            .Handle(new GetOperatorTourQuery(created.Value.TourId), CancellationToken.None);

        result.Value.ReviewBlockers.Should().Equal("primary_image_missing");
        result.Value.CanSubmitForReview.Should().BeFalse();
        result.Value.HasPrimaryImage.Should().BeFalse();
        result.Value.MediaCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithPrimaryImage_CanSubmit_AndAgreesWithSubmitReview()
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
            null, "Cover", 1, true, clock.UtcNow));
        await db.SaveChangesAsync();
        var get = new GetOperatorTourQueryHandler(db, user, clock);

        var before = await get.Handle(new GetOperatorTourQuery(created.Value.TourId), CancellationToken.None);
        var submit = await new TripMate.Application.Features.Operator.Tours.SubmitReview.SubmitTourForReviewCommandHandler(db, user, clock)
            .Handle(new TripMate.Application.Features.Operator.Tours.SubmitReview.SubmitTourForReviewCommand(created.Value.TourId),
                CancellationToken.None);
        var after = await get.Handle(new GetOperatorTourQuery(created.Value.TourId), CancellationToken.None);

        before.Value.CanSubmitForReview.Should().BeTrue();
        before.Value.ReviewBlockers.Should().BeEmpty();
        before.Value.HasPrimaryImage.Should().BeTrue();
        before.Value.MediaCount.Should().Be(1);
        submit.IsSuccess.Should().BeTrue("the detail hint and the command use the same evaluator");
        after.Value.CanSubmitForReview.Should().BeFalse("a Pending tour is no longer editable");
    }

    [Fact]
    public async Task Handle_ForAnotherOperator_HidesTour()
    {
        await using var db = TestDbContext.Create();
        var owner = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        var other = await SeedApprovedOperatorAsync(db, 8);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var create = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = owner.Id }, clock);
        var created = await create.Handle(Command(88, clock.UtcNow), CancellationToken.None);
        var handler = new GetOperatorTourQueryHandler(db,
            new FakeCurrentUserService { UserId = other.Id }, clock);

        var result = await handler.Handle(new GetOperatorTourQuery(created.Value.TourId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    private static CreateTourPackageCommand Command(long poiId, DateTimeOffset now) => new(
        "Da Nang day tour", "A full day visiting Da Nang landmarks.", null, 1_250_000m, 1,
        [TourAuthoringTestData.DestinationId, TourAuthoringTestData.SecondDestinationId],
        TourAuthoringTestData.PolicyTemplateId, " Hotel ", null,
        [new CreateTourItineraryItemRequest(1, 1, poiId, new TimeOnly(8, 0), 90, null)],
        [new CreateTourScheduleRequest(now.AddDays(2), now.AddDays(2).AddHours(8), "City center", 20)],
        Guid.NewGuid());

    private static async Task<User> SeedApprovedOperatorAsync(TestDbContext db, long id)
    {
        var user = new User { Id = id, Email = $"{id}@test.local", FullName = "Operator", Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile { UserId = id, User = user, CompanyName = "Tour Co", TaxCode = $"Tax{id}", BusinessLicenseNo = $"Lic{id}", ApprovalStatus = OperatorApprovalStatus.Approved });
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