using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Create;

public sealed class CreateTourPackageCommandHandlerTests
{
    [Fact]
    public async Task Handle_ForApprovedOperator_PersistsCompleteDraft()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());

        var result = await handler.Handle(Command(88), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Draft");
        var tour = await db.Tours.Include(candidate => candidate.Schedules)
            .Include(candidate => candidate.ItineraryItems).SingleAsync();
        tour.Status.Should().Be(TourStatus.Draft);
        tour.OperatorUserId.Should().Be(actor.Id);
        tour.Schedules.Should().ContainSingle();
        tour.Schedules.Single().MinimumCapacity.Should().Be(1);
        tour.Schedules.Single().AdultPrice.Should().Be(1_250_000m);
        tour.ItineraryItems.Should().ContainSingle(item => item.PoiId == 88);
    }

    [Fact]
    public async Task Handle_WhenPoiIsUnavailable_DoesNotPersistDraft()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());

        var result = await handler.Handle(Command(88), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        (await db.Tours.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenOperatorIsNotApproved_DoesNotPersistDraft()
    {
        await using var db = TestDbContext.Create();
        var actor = new User { Id = 7, Email = "operator@test.local", FullName = "Operator", Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(actor);
        await db.SaveChangesAsync();
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());

        var result = await handler.Handle(Command(88), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        (await db.Tours.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenCreateIsReplayedWithSameIdempotencyKey_ReturnsExistingDraft()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());
        var command = Command(88) with { IdempotencyKey = Guid.NewGuid() };

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.TourId.Should().Be(first.Value.TourId);
        (await db.Tours.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenIdempotencyKeyIsReusedForDifferentPayload_RejectsReplay()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());
        var command = Command(88) with { IdempotencyKey = Guid.NewGuid() };

        await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command with { Title = "Different title" }, CancellationToken.None);

        replay.IsFailure.Should().BeTrue();
        (await db.Tours.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_PersistsOrderedDestinationsTrimmedTermsAndPolicySnapshot()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());

        var result = await handler.Handle(Command(88) with
        {
            DestinationIds = [TourAuthoringTestData.SecondDestinationId, TourAuthoringTestData.DestinationId],
            Inclusions = "  Hotel and breakfast  ",
            Exclusions = "   ",
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var tour = await db.Tours.Include(candidate => candidate.Destinations)
            .Include(candidate => candidate.CancellationPolicyRules).SingleAsync();
        tour.Destinations.OrderBy(link => link.SequenceNo).Select(link => link.DestinationId)
            .Should().Equal(TourAuthoringTestData.SecondDestinationId, TourAuthoringTestData.DestinationId);
        tour.DestinationSummary.Should().Be("Hoi An, Da Nang");
        tour.Inclusions.Should().Be("Hotel and breakfast");
        tour.Exclusions.Should().BeNull();
        tour.CancellationPolicyTemplateId.Should().Be(TourAuthoringTestData.PolicyTemplateId);
        tour.CancellationPolicyRules.Should().HaveCount(3);
    }

    [Fact]
    public async Task Handle_PersistsHighlightsTravellerRequirementsAndTimeZone()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());

        var result = await handler.Handle(Command(88) with
        {
            Highlights = " Golden Bridge ",
            TravellerRequirements = "Passport required",
            TimeZoneId = "Asia/Bangkok",
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var tour = await db.Tours.SingleAsync();
        tour.Highlights.Should().Be("Golden Bridge");
        tour.TravellerRequirements.Should().Be("Passport required");
        tour.TimeZoneId.Should().Be("Asia/Bangkok");
    }

    [Fact]
    public async Task Handle_WhenReplayChangesATermsField_RejectsAsPayloadMismatch()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());
        var command = Command(88) with { Highlights = "A" };
        (await handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var replay = await handler.Handle(command with { Highlights = "B" }, CancellationToken.None);

        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be("operator_tour.idempotency_key_payload_mismatch");
    }

    [Fact]
    public async Task Handle_WhenDestinationIsUnknown_ReturnsNotFoundAndPersistsNothing()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());

        var result = await handler.Handle(Command(88) with
        {
            DestinationIds = [TourAuthoringTestData.DestinationId, 999],
        }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.destination_not_found");
        (await db.Tours.CountAsync()).Should().Be(0);
        (await db.TourPackageCreateOperations.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(999L, true)]
    [InlineData(1L, false)]
    public async Task Handle_WhenPolicyIsUnknownOrInactive_ReturnsUnavailableAndPersistsNothing(
        long templateId, bool seedActive)
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        db.Destinations.Add(TourAuthoringTestData.NewDestination(TourAuthoringTestData.DestinationId, "Da Nang"));
        db.CancellationPolicyTemplates.Add(TourAuthoringTestData.NewPolicy(1, active: seedActive));
        await db.SaveChangesAsync();
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());

        var result = await handler.Handle(Command(88) with { CancellationPolicyTemplateId = templateId },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.cancellation_policy_unavailable");
        (await db.Tours.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenReplayOnlyDiffersByWhitespace_ReturnsTheOriginalDraft()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());
        var command = Command(88) with { IdempotencyKey = Guid.NewGuid(), Inclusions = " Meals ", Exclusions = "  " };

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command with { Inclusions = "Meals", Exclusions = null },
            CancellationToken.None);

        replay.IsSuccess.Should().BeTrue();
        replay.Value.TourId.Should().Be(first.Value.TourId);
        (await db.Tours.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenReplayReordersDestinations_RejectsAsPayloadMismatch()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var handler = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());
        var command = Command(88) with
        {
            IdempotencyKey = Guid.NewGuid(),
            DestinationIds = [TourAuthoringTestData.DestinationId, TourAuthoringTestData.SecondDestinationId],
        };

        await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command with
        {
            DestinationIds = [TourAuthoringTestData.SecondDestinationId, TourAuthoringTestData.DestinationId],
        }, CancellationToken.None);

        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be("operator_tour.idempotency_key_payload_mismatch");
        (await db.Tours.CountAsync()).Should().Be(1);
    }

    private static CreateTourPackageCommand Command(long poiId) => new(
        "Da Nang day tour", "A full day visiting Da Nang landmarks.", null, 1_250_000m, 1,
        [TourAuthoringTestData.DestinationId], TourAuthoringTestData.PolicyTemplateId, null, null,
        [new CreateTourItineraryItemRequest(1, 1, poiId, new TimeOnly(8, 0), 90, null)],
        [new CreateTourScheduleRequest(DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(2).AddHours(8), "City center", 20)],
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