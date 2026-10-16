using System.Reflection;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Features.Tours.GetDetail;
using TripMate.Application.Features.Tours.Shared;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Tours.GetDetail;

public sealed class GetTourDetailQueryHandlerTests
{
    private static readonly DateTimeOffset Now = PublicTourTestData.Now;

    private static GetTourDetailQueryHandler CreateHandler(TestDbContext db) =>
        new(db, new FakeDateTimeProvider { UtcNow = Now }, NullLogger<GetTourDetailQueryHandler>.Instance);

    private static async Task<TestDbContext> CreateDatabaseAsync()
    {
        var db = TestDbContext.Create();
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await PublicTourTestData.SeedOperatorAsync(db, 7);
        await PublicTourTestData.AddPoiAsync(db, 100, "Hoi An Ancient Town", "Tran Phu, Hoi An");
        await PublicTourTestData.AddPoiAsync(db, 101, "Marble Mountains", null);
        return db;
    }

    private static void SetSchedule(TourSchedule schedule, int? reserved = null, TourScheduleStatus? status = null,
        DateTimeOffset? registrationDeadline = null)
    {
        if (reserved.HasValue)
            typeof(TourSchedule).GetProperty(nameof(TourSchedule.ReservedCapacity))!.SetValue(schedule, reserved.Value);
        if (status.HasValue)
            typeof(TourSchedule).GetProperty(nameof(TourSchedule.Status))!.SetValue(schedule, status.Value);
        if (registrationDeadline.HasValue)
            typeof(TourSchedule).GetProperty(nameof(TourSchedule.RegistrationDeadlineAtUtc))!
                .SetValue(schedule, registrationDeadline.Value);
    }

    [Fact]
    public async Task Handle_ForAPublishedTour_ReturnsEveryDetailSection()
    {
        await using var db = await CreateDatabaseAsync();
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published);
        db.TourMedia.Add(TripMate.Domain.Entities.TourMedia.Create(tour, "tour/a", "https://cdn.example.test/a.jpg", "Old town", "Old town at dusk", 2, false, Now));
        db.TourMedia.Add(TripMate.Domain.Entities.TourMedia.Create(tour, "tour/b", "https://cdn.example.test/b.jpg", null, "Lantern street", 1, true, Now));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;
        dto.TourId.Should().Be(tour.Id.ToString());
        dto.Title.Should().Be("Hoi An food tour");
        dto.Description.Should().Be("A relaxed two day food tour.");
        dto.Destination.Should().Be("Hoi An, Da Nang");
        dto.Destinations.Should().Equal("Hoi An", "Da Nang");
        dto.DurationDays.Should().Be(2);
        dto.BasePrice.Should().Be(1_500_000);
        dto.Currency.Should().Be("VND");
        dto.Inclusions.Should().Be("Breakfast and guide");
        dto.Exclusions.Should().Be("Personal spending");
        dto.Operator.Name.Should().Be("Hoi An Tours");
        dto.Operator.ContactPhone.Should().Be("0900000000");
        dto.Images.Select(image => image.Url).Should().Equal(
            "https://cdn.example.test/b.jpg", "https://cdn.example.test/a.jpg");
        dto.Images[0].IsPrimary.Should().BeTrue();
        dto.CancellationPolicy.Rules.Select(rule => (rule.DaysBeforeDeparture, rule.CancellationFeePercent))
            .Should().Equal((30, 0m), (15, 25m), (0, 100m));
        dto.AsOfUtc.Should().Be(Now.UtcDateTime);
        dto.Rating.Count.Should().Be(0);
        dto.Rating.Average.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ReturnsTheItineraryByDayAndSequenceWithPoiDetails()
    {
        await using var db = await CreateDatabaseAsync();
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published);

        var dto = (await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None)).Value;

        dto.Itinerary.Select(day => day.DayNo).Should().Equal(1, 2);
        dto.Itinerary[0].Items.Select(item => item.SequenceNo).Should().Equal(1, 2);
        var first = dto.Itinerary[0].Items[0];
        first.Poi.PoiId.Should().Be("100");
        first.Poi.Name.Should().Be("Hoi An Ancient Town");
        first.Poi.Address.Should().Be("Tran Phu, Hoi An");
        first.Poi.Latitude.Should().Be(15.88m);
        first.PlannedTime.Should().Be("08:30");
        first.StayDurationMinutes.Should().Be(90);
        first.Notes.Should().Be("Local guide");
        dto.Itinerary[0].Items[1].Poi.Address.Should().BeNull();
    }

    [Fact]
    public async Task Handle_StillShowsAStopWhoseCatalogueEntryWasDeactivated()
    {
        await using var db = await CreateDatabaseAsync();
        await PublicTourTestData.AddPoiAsync(db, 102, "Closed Pagoda", "Hue", PointOfInterestStatus.Inactive);
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published,
            stops: [(1, 1, 102L), (1, 2, 100L)]);

        var dto = (await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None)).Value;

        dto.Itinerary.Single().Items.Select(item => item.Poi.Name)
            .Should().Equal("Closed Pagoda", "Hoi An Ancient Town");
    }

    [Theory]
    [InlineData(TourStatus.Draft)]
    [InlineData(TourStatus.Pending)]
    [InlineData(TourStatus.Rejected)]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Inactive)]
    public async Task Handle_ForANonPublicTour_ReturnsNotFound(TourStatus status)
    {
        await using var db = await CreateDatabaseAsync();
        var tour = await PublicTourTestData.AddTourAsync(db, 7, status);

        var result = await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourErrorCodes.NotFound);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(987654321L)]
    public async Task Handle_ForAnUnknownOrInvalidId_ReturnsNotFound(long id)
    {
        await using var db = await CreateDatabaseAsync();
        await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published);

        var result = await CreateHandler(db).Handle(new GetTourDetailQuery(id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_ForAnUnapprovedOperator_ReturnsNotFound()
    {
        await using var db = TestDbContext.Create();
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await PublicTourTestData.SeedOperatorAsync(db, 8, OperatorApprovalStatus.PendingApproval);
        var tour = await PublicTourTestData.AddTourAsync(db, 8, TourStatus.Published);

        var result = await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(TourErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_OnlyAnotherVisibleVersionIsServed()
    {
        await using var db = await CreateDatabaseAsync();
        var published = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published, "Version 1");
        var draftVersion = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Draft, "Version 2");
        draftVersion.SetVersionLineage(published.Id, 2);
        await db.SaveChangesAsync();

        var visible = await CreateHandler(db).Handle(new GetTourDetailQuery(published.Id), CancellationToken.None);
        var hidden = await CreateHandler(db).Handle(new GetTourDetailQuery(draftVersion.Id), CancellationToken.None);

        visible.IsSuccess.Should().BeTrue();
        hidden.ErrorCode.Should().Be(TourErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_ListsOnlyFutureScheduledDeparturesInStartOrder()
    {
        await using var db = await CreateDatabaseAsync();
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published, departures:
        [
            (Now.AddDays(30), 20, 1_000_000m),
            (Now.AddDays(5), 20, 1_200_000m),
            (Now.AddDays(-3), 20, 1_200_000m),
            (Now.AddDays(8), 20, 1_200_000m),
            (Now.AddDays(9), 20, 1_200_000m),
        ]);
        var schedules = tour.Schedules.ToList();
        SetSchedule(schedules[3], status: TourScheduleStatus.Cancelled);
        SetSchedule(schedules[4], status: TourScheduleStatus.Completed);
        await db.SaveChangesAsync();

        var dto = (await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None)).Value;

        dto.Schedules.Select(schedule => schedule.StartAtUtc)
            .Should().Equal(Now.AddDays(5).UtcDateTime, Now.AddDays(30).UtcDateTime);
    }

    [Fact]
    public async Task Handle_ClassifiesAvailabilityAndBookability()
    {
        await using var db = await CreateDatabaseAsync();
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published, departures:
        [
            (Now.AddDays(1), 10, 1_000_000m),
            (Now.AddDays(2), 10, 1_000_000m),
            (Now.AddDays(3), 10, 1_000_000m),
            (Now.AddDays(4), 10, 1_000_000m),
        ]);
        var schedules = tour.Schedules.ToList();
        SetSchedule(schedules[0], reserved: 3);
        SetSchedule(schedules[1], reserved: 10);
        SetSchedule(schedules[2], reserved: 11);
        SetSchedule(schedules[3], reserved: 0, registrationDeadline: Now.AddHours(-1));
        await db.SaveChangesAsync();

        var dto = (await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None)).Value;

        dto.Schedules.Select(schedule => (schedule.AvailabilityStatus, schedule.RemainingSlots, schedule.Bookable))
            .Should().Equal(
                (TourAvailability.Available, (int?)7, true),
                (TourAvailability.SoldOut, (int?)0, false),
                (TourAvailability.Unknown, (int?)null, false),
                (TourAvailability.Available, (int?)10, false));
        dto.HasBookableSchedule.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenEveryDepartureIsSoldOut_NoScheduleIsBookable()
    {
        await using var db = await CreateDatabaseAsync();
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published);
        SetSchedule(tour.Schedules.Single(), reserved: 20);
        await db.SaveChangesAsync();

        var dto = (await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None)).Value;

        dto.HasBookableSchedule.Should().BeFalse();
        dto.Schedules.Single().Bookable.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WithNoUpcomingDeparture_ReturnsTheTourWithNoSchedules()
    {
        await using var db = await CreateDatabaseAsync();
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published,
            departures: [(Now.AddDays(-5), 20, 1_000_000m)]);

        var dto = (await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None)).Value;

        dto.Schedules.Should().BeEmpty();
        dto.HasBookableSchedule.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_FallsBackToTheBasePriceForALegacyDepartureWithoutAnAdultPrice()
    {
        await using var db = await CreateDatabaseAsync();
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.AdultPrice))!.SetValue(tour.Schedules.Single(), null);
        await db.SaveChangesAsync();

        var dto = (await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None)).Value;

        dto.Schedules.Single().AdultPrice.Should().Be(1_500_000);
        dto.Schedules.Single().ChildPrice.Should().Be(900_000);
        dto.Schedules.Single().InfantPrice.Should().Be(0);
    }

    [Fact]
    public async Task Handle_DoesNotReturnDeletedMedia()
    {
        await using var db = await CreateDatabaseAsync();
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published);
        var live = TripMate.Domain.Entities.TourMedia.Create(tour, "tour/live", "https://cdn.example.test/live.jpg", null, "Live", 1, true, Now);
        var removed = TripMate.Domain.Entities.TourMedia.Create(tour, "tour/gone", "https://cdn.example.test/gone.jpg", null, "Gone", 2, false, Now);
        removed.SoftDelete(Now);
        db.TourMedia.AddRange(live, removed);
        await db.SaveChangesAsync();

        var dto = (await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None)).Value;

        dto.Images.Select(image => image.Url).Should().Equal("https://cdn.example.test/live.jpg");
    }

    [Fact]
    public void OperatorDto_ExposesOnlyNameAndContactPhone()
    {
        typeof(TourDetailOperatorDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().BeEquivalentTo(nameof(TourDetailOperatorDto.Name), nameof(TourDetailOperatorDto.ContactPhone));
    }

    [Fact]
    public async Task Handle_NeverLeaksOperatorTaxLicenceOrAddress()
    {
        await using var db = await CreateDatabaseAsync();
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published);

        var dto = (await CreateHandler(db).Handle(new GetTourDetailQuery(tour.Id), CancellationToken.None)).Value;

        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        json.Should().NotContain("TAX-7").And.NotContain("LIC-7").And.NotContain("1 Private Street");
    }
}