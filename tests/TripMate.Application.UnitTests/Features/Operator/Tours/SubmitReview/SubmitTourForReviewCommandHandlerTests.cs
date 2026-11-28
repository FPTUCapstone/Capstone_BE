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
        await SeedReadyChannelAsync(db, actor.Id);
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

        var result = await handler.Handle(new SubmitTourForReviewCommand(created.Value.TourId, "  Please review  "), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.Tours.SingleAsync()).Status.Should().Be(TourStatus.Pending);
        tour.SubmissionNote.Should().Be("Please review");
        tour.SubmittedAtUtc.Should().Be(clock.UtcNow);
        result.Value.SubmittedAtUtc.Should().Be(clock.UtcNow);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        var duplicate = await handler.Handle(new SubmitTourForReviewCommand(tour.Id, "different"), CancellationToken.None);
        duplicate.IsFailure.Should().BeTrue();
        tour.SubmissionNote.Should().Be("Please review");
        tour.SubmittedAtUtc.Should().Be(clock.UtcNow.AddHours(-1));
        (await db.AuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenRejectedTourIsResubmitted_AuditRecordsRejectedAsTheBeforeStatus()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        await SeedReadyChannelAsync(db, actor.Id);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var user = new FakeCurrentUserService { UserId = actor.Id };
        var created = await new CreateTourPackageCommandHandler(db, user, clock)
            .Handle(Command(88, clock.UtcNow), CancellationToken.None);
        var tour = await db.Tours.SingleAsync();
        db.TourMedia.Add(TripMate.Domain.Entities.TourMedia.Create(tour, "tour/cover", "https://cdn.example.test/cover.jpg",
            null, "Da Nang tour cover", 1, true, clock.UtcNow));
        await db.SaveChangesAsync();
        var handler = new SubmitTourForReviewCommandHandler(db, user, clock);
        (await handler.Handle(new SubmitTourForReviewCommand(created.Value.TourId, "Original submission"), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        tour.Reject(99, "Needs a clearer description.", clock.UtcNow);
        await db.SaveChangesAsync();

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var resubmitted = await handler.Handle(new SubmitTourForReviewCommand(created.Value.TourId, "   "), CancellationToken.None);

        resubmitted.IsSuccess.Should().BeTrue();
        tour.SubmittedAtUtc.Should().Be(clock.UtcNow);
        tour.SubmissionNote.Should().BeNull();
        tour.RejectionReason.Should().BeNull();
        tour.ReviewedBy.Should().BeNull();
        tour.ReviewedAtUtc.Should().BeNull();
        var audits = await db.AuditLogs.OrderBy(log => log.Id).ToListAsync();
        audits.Should().HaveCount(2);
        audits[0].BeforeData.Should().Contain("Draft");
        audits[1].BeforeData.Should().Contain("Rejected").And.NotContain("Draft");
        audits[1].BeforeData.Should().Contain("Original submission").And.Contain("Needs a clearer description.")
            .And.Contain("ReviewedBy").And.Contain("SubmittedAtUtc");
        audits[1].AfterData.Should().Contain("Pending").And.Contain("SubmissionNote").And.Contain("ReviewedAtUtc");
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

    [Theory]
    [InlineData("title", "required_fields_missing")]
    [InlineData("description", "required_fields_missing")]
    [InlineData("price", "required_fields_missing")]
    [InlineData("itinerary", "itinerary_missing")]
    [InlineData("end", "future_departure_missing")]
    [InlineData("capacity", "future_departure_missing")]
    [InlineData("childPrice", "future_departure_missing")]
    [InlineData("deadline", "future_departure_missing")]
    public async Task Handle_IncompleteLegacyDraft_DetailAndSubmitAgreeAndDoNotMutate(string missing, string blocker)
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
        var schedule = await db.TourSchedules.SingleAsync();
        switch (missing)
        {
            case "title": typeof(Tour).GetProperty(nameof(Tour.Title))!.SetValue(tour, " "); break;
            case "description": typeof(Tour).GetProperty(nameof(Tour.Description))!.SetValue(tour, null); break;
            case "price": typeof(Tour).GetProperty(nameof(Tour.BasePrice))!.SetValue(tour, 0m); break;
            case "itinerary": db.TourItineraryItems.RemoveRange(await db.TourItineraryItems.ToListAsync()); break;
            case "end": typeof(TourSchedule).GetProperty(nameof(TourSchedule.EndAtUtc))!.SetValue(schedule, schedule.StartAtUtc); break;
            case "capacity": typeof(TourSchedule).GetProperty(nameof(TourSchedule.TotalCapacity))!.SetValue(schedule, 0); break;
            case "childPrice": typeof(TourSchedule).GetProperty(nameof(TourSchedule.ChildPrice))!.SetValue(schedule, -1m); break;
            case "deadline": typeof(TourSchedule).GetProperty(nameof(TourSchedule.RegistrationDeadlineAtUtc))!.SetValue(schedule, schedule.StartAtUtc); break;
        }
        await db.SaveChangesAsync();
        var before = tour.UpdatedAtUtc;
        var detail = await new TripMate.Application.Features.Operator.Tours.Get.GetOperatorTourQueryHandler(db, user, clock)
            .Handle(new TripMate.Application.Features.Operator.Tours.Get.GetOperatorTourQuery(tour.Id), CancellationToken.None);
        detail.Value.ReviewBlockers.Should().Contain(blocker);
        detail.Value.CanSubmitForReview.Should().BeFalse();
        var result = await new SubmitTourForReviewCommandHandler(db, user, clock)
            .Handle(new SubmitTourForReviewCommand(created.Value.TourId), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        tour.Status.Should().Be(TourStatus.Draft);
        tour.UpdatedAtUtc.Should().Be(before);
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenNoReadyPaymentChannel_BlocksSubmission()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        // No Ready channel seeded — gate should block
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var user = new FakeCurrentUserService { UserId = actor.Id };
        var created = await new CreateTourPackageCommandHandler(db, user, clock)
            .Handle(Command(88, clock.UtcNow), CancellationToken.None);
        var tour = await db.Tours.SingleAsync();
        db.TourMedia.Add(TripMate.Domain.Entities.TourMedia.Create(tour, "tour/cover", "https://cdn.example.test/cover.jpg",
            null, "Da Nang tour cover", 1, true, clock.UtcNow));
        await db.SaveChangesAsync();
        var handler = new SubmitTourForReviewCommandHandler(db, user, clock);

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

    private static async Task SeedReadyChannelAsync(TestDbContext db, long operatorUserId)
    {
        var now = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        var ch = OperatorPaymentChannel.Create(operatorUserId, now);
        ch.SetCredentialRefs("ref:client-1", "ref:api-1", "ref:checksum-1", "123456789", "TO Account", "VCB", now);
        ch.MarkReady("123456789", "TO Account", "VCB", "https://hook.example.test/webhook", now);
        db.OperatorPaymentChannels.Add(ch);
        await db.SaveChangesAsync();
    }
}