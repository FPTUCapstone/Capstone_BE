using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Admin.Tours.Review.PendingTours;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using DomainTourMedia = TripMate.Domain.Entities.TourMedia;

namespace TripMate.Application.UnitTests.Features.Admin.Tours.Review;

public sealed class GetPendingTourReviewQueryHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private static GetPendingTourReviewQueryHandler CreateHandler(
        TestDbContext db, long? userId = 55, string? role = nameof(UserRole.Staff)) =>
        new(db, new FakeCurrentUserService { UserId = userId, Role = role });

    private static async Task<Tour> AddPendingTourAsync(TestDbContext db, long operatorId = 1)
    {
        var tour = PublicTourTestData.NewTour(operatorId, "Review me");
        tour.SubmitForReview(T0);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        return tour;
    }

    [Fact]
    public async Task Handle_ReportsTheRealSubmissionTime_NotTheLastUpdateTime()
    {
        await using var db = NewDb();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        await PublicTourTestData.AddPoiAsync(db, 100, "Ancient Town", null);
        await PublicTourTestData.AddPoiAsync(db, 101, "Bridge Pagoda", null);
        var tour = await AddPendingTourAsync(db);
        Set(tour, nameof(Tour.UpdatedAtUtc), T0.AddHours(6));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new GetPendingTourReviewQuery(tour.Id), CancellationToken.None);

        result.Value.SubmittedAtUtc.Should().Be(T0, "SubmittedAtUtc is the submission time; UpdatedAtUtc moves with later edits");
    }

    [Fact]
    public async Task Handle_FallsBackToUpdatedAtForLegacyRowsWithoutASubmissionTime()
    {
        await using var db = NewDb();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        await PublicTourTestData.AddPoiAsync(db, 100, "Ancient Town", null);
        await PublicTourTestData.AddPoiAsync(db, 101, "Bridge Pagoda", null);
        var tour = await AddPendingTourAsync(db);
        Set<DateTimeOffset?>(tour, nameof(Tour.SubmittedAtUtc), null);
        Set(tour, nameof(Tour.UpdatedAtUtc), T0.AddHours(6));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new GetPendingTourReviewQuery(tour.Id), CancellationToken.None);

        result.Value.SubmittedAtUtc.Should().Be(T0.AddHours(6));
    }

    /// <summary>The detail read requires an ACTIVE Staff account in the database (same rule as the TM-83 queue).</summary>
    private static TestDbContext NewDb(AccountStatus staffStatus = AccountStatus.Active)
    {
        var db = TestDbContext.Create();
        db.Users.Add(new User { Id = 55, Email = "staff@test.local", FullName = "Staff", Role = UserRole.Staff, Status = staffStatus });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task Handle_WhenStaffAccountIsLocked_ReturnsForbidden()
    {
        await using var db = NewDb(AccountStatus.Locked);

        var result = await CreateHandler(db).Handle(new GetPendingTourReviewQuery(1), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AdminAccessRequired);
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property)!.SetValue(instance, value);

    [Theory]
    [InlineData(null, null)]
    [InlineData(99L, nameof(UserRole.Administrator))]
    [InlineData(99L, nameof(UserRole.Traveler))]
    [InlineData(99L, nameof(UserRole.TourOperator))]
    public async Task Handle_WhenCallerNotStaff_ReturnsForbidden(long? userId, string? role)
    {
        await using var db = NewDb();

        var result = await CreateHandler(db, userId, role)
            .Handle(new GetPendingTourReviewQuery(1), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AdminAccessRequired);
    }

    [Fact]
    public async Task Handle_WhenTourMissing_ReturnsNotFound()
    {
        await using var db = NewDb();

        var result = await CreateHandler(db).Handle(new GetPendingTourReviewQuery(404), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorTourErrorCodes.TourNotFound);
    }

    [Theory]
    [InlineData(TourStatus.Draft)]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Rejected)]
    [InlineData(TourStatus.Published)]
    public async Task Handle_WhenTourNotPending_ReturnsInvalidState(TourStatus status)
    {
        await using var db = NewDb();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, status);

        var result = await CreateHandler(db).Handle(new GetPendingTourReviewQuery(tour.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorTourErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Handle_MapsEveryChecklistInput()
    {
        await using var db = NewDb();
        await PublicTourTestData.SeedOperatorAsync(db, 1, company: "Sunrise Travel");
        await PublicTourTestData.AddPoiAsync(db, 100, "Ancient Town", "1 Old Street");
        await PublicTourTestData.AddPoiAsync(db, 101, "Bridge Pagoda", null);
        var tour = await AddPendingTourAsync(db);
        db.TourMedia.AddRange(
            DomainTourMedia.Create(tour, "id/b", "https://res.cloudinary.com/x/b.webp", "Second", "alt b", 2, false, T0),
            DomainTourMedia.Create(tour, "id/a", "https://res.cloudinary.com/x/a.webp", "First", "alt a", 1, true, T0));
        var deleted = DomainTourMedia.Create(tour, "id/c", "https://res.cloudinary.com/x/c.webp", null, "alt c", 3, false, T0);
        deleted.SoftDelete(T0);
        db.TourMedia.Add(deleted);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new GetPendingTourReviewQuery(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");
        var dto = result.Value;
        dto.TourId.Should().Be(tour.Id);
        dto.TourName.Should().Be("Review me");
        dto.OperatorName.Should().Be("Sunrise Travel");
        dto.SubmittedAtUtc.Should().Be(T0);
        dto.PriceVnd.Should().Be(1_500_000m);
        dto.Overview.Should().Be("A relaxed two day food tour.");
        dto.ImageCount.Should().Be(2);

        dto.Images.Select(i => i.Url).Should().Equal(
            "https://res.cloudinary.com/x/a.webp", "https://res.cloudinary.com/x/b.webp");
        dto.Images[0].IsPrimary.Should().BeTrue();
        dto.Images[0].AltText.Should().Be("alt a");

        dto.CancellationPolicy.Select(r => (r.DaysBeforeDeparture, r.FeePercent))
            .Should().Equal([(30, 0m), (15, 25m), (0, 100m)]);

        dto.Schedules.Should().ContainSingle();
        dto.Schedules[0].Capacity.Should().Be(20);
        dto.Schedules[0].MeetingPoint.Should().Be("Hotel lobby");
        dto.Schedules[0].AdultPrice.Should().Be(1_500_000m);

        dto.Itinerary.Select(s => (s.DayNo, s.SequenceNo, s.PoiName)).Should().Equal(
            (1, 1, "Ancient Town"), (1, 2, "Bridge Pagoda"), (2, 1, "Ancient Town"));
        dto.Itinerary[0].StayMinutes.Should().Be(90);
        dto.Itinerary[0].PlannedTime.Should().Be(new TimeOnly(8, 30));
        dto.Itinerary[0].Notes.Should().Be("Local guide");
    }

    [Fact]
    public async Task Handle_SplitsServicesOnLineBreaksAndDropsBlanks()
    {
        await using var db = NewDb();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        await PublicTourTestData.AddPoiAsync(db, 100, "Ancient Town", null);
        await PublicTourTestData.AddPoiAsync(db, 101, "Bridge Pagoda", null);
        var tour = await AddPendingTourAsync(db);
        Set(tour, nameof(Tour.Inclusions), "Breakfast\r\n\r\n  Guide  \n");
        Set<string?>(tour, nameof(Tour.Exclusions), null);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new GetPendingTourReviewQuery(tour.Id), CancellationToken.None);

        result.Value.IncludedServices.Should().Equal("Breakfast", "Guide");
        result.Value.ExcludedServices.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ResponseContainsNoOperatorContactData()
    {
        await using var db = NewDb();
        await PublicTourTestData.SeedOperatorAsync(db, 1, phone: "0900123456", address: "9 Secret Lane");
        await PublicTourTestData.AddPoiAsync(db, 100, "Ancient Town", null);
        await PublicTourTestData.AddPoiAsync(db, 101, "Bridge Pagoda", null);
        var tour = await AddPendingTourAsync(db);

        var result = await CreateHandler(db).Handle(new GetPendingTourReviewQuery(tour.Id), CancellationToken.None);

        var json = JsonSerializer.Serialize(result.Value);
        json.Should().NotContain("0900123456")
            .And.NotContain("9 Secret Lane")
            .And.NotContain("TAX-1")
            .And.NotContain("LIC-1")
            .And.NotContain("operator-1@test.local");
    }

    [Fact]
    public async Task Handle_TourWithNothingOptional_ReturnsEmptyCollections()
    {
        await using var db = NewDb();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await AddPendingTourAsync(db);
        db.TourSchedules.RemoveRange(db.TourSchedules.Where(s => s.TourId == tour.Id));
        db.TourItineraryItems.RemoveRange(db.TourItineraryItems.Where(i => i.TourId == tour.Id));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new GetPendingTourReviewQuery(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");
        result.Value.Images.Should().BeEmpty();
        result.Value.Schedules.Should().BeEmpty();
        result.Value.Itinerary.Should().BeEmpty();
        result.Value.NextDepartureAtUtc.Should().BeNull();
    }
}