using FluentAssertions;

using TripMate.Application.Features.Admin.Tours.Review.PendingTours;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using DomainTourMedia = TripMate.Domain.Entities.TourMedia;

namespace TripMate.Application.UnitTests.Features.Admin.Tours.Review;

public sealed class GetPendingToursQueryHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private static GetPendingToursQueryHandler CreateHandler(
        TestDbContext db, long? userId = 55, string? role = nameof(UserRole.Staff)) =>
        new(db, new FakeCurrentUserService { UserId = userId, Role = role });

    private static async Task<Tour> AddTourAsync(
        TestDbContext db, long operatorId, string title, TourStatus status, DateTimeOffset submittedAtUtc)
    {
        var tour = PublicTourTestData.NewTour(operatorId, title);
        if (status != TourStatus.Draft)
        {
            tour.SubmitForReview(submittedAtUtc);
        }

        if (status == TourStatus.Approved)
        {
            tour.Approve(2, submittedAtUtc);
        }
        else if (status == TourStatus.Rejected)
        {
            tour.Reject(2, "Missing details", submittedAtUtc);
        }

        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        return tour;
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(99L, nameof(UserRole.Administrator))]
    [InlineData(99L, nameof(UserRole.Traveler))]
    [InlineData(99L, nameof(UserRole.TourOperator))]
    public async Task Handle_WhenCallerNotStaff_ReturnsForbidden(long? userId, string? role)
    {
        await using var db = TestDbContext.Create();

        var result = await CreateHandler(db, userId, role).Handle(new GetPendingToursQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AdminAccessRequired);
    }

    [Fact]
    public async Task Handle_ListsOnlyPendingTours()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var pending = await AddTourAsync(db, 1, "Pending tour", TourStatus.Pending, T0);
        await AddTourAsync(db, 1, "Draft tour", TourStatus.Draft, T0);
        await AddTourAsync(db, 1, "Approved tour", TourStatus.Approved, T0);
        await AddTourAsync(db, 1, "Rejected tour", TourStatus.Rejected, T0);

        var result = await CreateHandler(db).Handle(new GetPendingToursQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");
        result.Value.Items.Select(i => i.TourId).Should().Equal(pending.Id);
        result.Value.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_OrdersOldestSubmittedFirstThenById()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var newest = await AddTourAsync(db, 1, "Newest", TourStatus.Pending, T0.AddHours(5));
        var oldestA = await AddTourAsync(db, 1, "Oldest A", TourStatus.Pending, T0);
        var oldestB = await AddTourAsync(db, 1, "Oldest B", TourStatus.Pending, T0);
        var middle = await AddTourAsync(db, 1, "Middle", TourStatus.Pending, T0.AddHours(2));

        var result = await CreateHandler(db).Handle(new GetPendingToursQuery(), CancellationToken.None);

        result.Value.Items.Select(i => i.TourId).Should().Equal(oldestA.Id, oldestB.Id, middle.Id, newest.Id);
    }

    [Fact]
    public async Task Handle_FiltersByTitleOrOperatorCompanyName()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1, company: "Sunrise Travel");
        await PublicTourTestData.SeedOperatorAsync(db, 2, company: "Lotus Journeys");
        var byTitle = await AddTourAsync(db, 1, "Hoi An lantern night", TourStatus.Pending, T0);
        var byCompany = await AddTourAsync(db, 2, "Mountain trek", TourStatus.Pending, T0.AddMinutes(1));
        await AddTourAsync(db, 1, "Unrelated cooking class", TourStatus.Pending, T0.AddMinutes(2));

        var title = await CreateHandler(db).Handle(new GetPendingToursQuery(Keyword: "  lantern "), CancellationToken.None);
        var company = await CreateHandler(db).Handle(new GetPendingToursQuery(Keyword: "lotus"), CancellationToken.None);

        title.Value.Items.Select(i => i.TourId).Should().Equal(byTitle.Id);
        company.Value.Items.Select(i => i.TourId).Should().Equal(byCompany.Id);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    public async Task Handle_TreatsLikeWildcardsInKeywordLiterally(string keyword)
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        await AddTourAsync(db, 1, "Plain title", TourStatus.Pending, T0);
        await AddTourAsync(db, 1, "Another title", TourStatus.Pending, T0.AddMinutes(1));

        var result = await CreateHandler(db).Handle(new GetPendingToursQuery(Keyword: keyword), CancellationToken.None);

        result.Value.Items.Should().BeEmpty("a wildcard typed by the reviewer must not match every pending tour");
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_PagesWithCorrectTotals()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        for (var i = 0; i < 25; i++)
        {
            await AddTourAsync(db, 1, $"Tour {i:00}", TourStatus.Pending, T0.AddMinutes(i));
        }

        var page2 = await CreateHandler(db).Handle(new GetPendingToursQuery(PageNumber: 2, PageSize: 20), CancellationToken.None);

        page2.Value.Items.Should().HaveCount(5);
        page2.Value.TotalCount.Should().Be(25);
        page2.Value.TotalPages.Should().Be(2);
        page2.Value.PageNumber.Should().Be(2);
        page2.Value.PageSize.Should().Be(20);
        page2.Value.Items[0].TourName.Should().Be("Tour 20");
    }

    [Fact]
    public async Task Handle_PageBeyondLastReturnsEmptyItemsWithTotals()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        await AddTourAsync(db, 1, "Only tour", TourStatus.Pending, T0);

        var result = await CreateHandler(db).Handle(new GetPendingToursQuery(PageNumber: 9), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(1);
        result.Value.TotalPages.Should().Be(1);
    }

    [Fact]
    public async Task Handle_MapsSummaryFields()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1, company: "Sunrise Travel");
        var departures = new (DateTimeOffset Start, int Total, decimal? Adult)[]
        {
            (T0.AddDays(30), 12, 2_000_000m),
            (T0.AddDays(10), 8, 1_500_000m),
        };
        var tour = PublicTourTestData.NewTour(1, "Mapped tour", departures);
        tour.SubmitForReview(T0);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        db.TourMedia.AddRange(
            DomainTourMedia.Create(tour, "id/1", "https://res.cloudinary.com/x/1.webp", null, "alt 1", 1, true, T0),
            DomainTourMedia.Create(tour, "id/2", "https://res.cloudinary.com/x/2.webp", null, "alt 2", 2, false, T0));
        var deleted = DomainTourMedia.Create(tour, "id/3", "https://res.cloudinary.com/x/3.webp", null, "alt 3", 3, false, T0);
        deleted.SoftDelete(T0);
        db.TourMedia.Add(deleted);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new GetPendingToursQuery(), CancellationToken.None);

        var item = result.Value.Items.Should().ContainSingle().Subject;
        item.TourId.Should().Be(tour.Id);
        item.TourName.Should().Be("Mapped tour");
        item.OperatorName.Should().Be("Sunrise Travel");
        item.Location.Should().Be("Hoi An, Da Nang");
        item.SubmittedAtUtc.Should().Be(T0);
        item.DurationDays.Should().Be(2);
        item.PriceVnd.Should().Be(1_500_000m);
        item.NextDepartureAtUtc.Should().Be(T0.AddDays(10), "the earliest departure is reported");
        item.NextDepartureCapacity.Should().Be(8, "capacity belongs to that earliest departure");
        item.ImageCount.Should().Be(2, "soft-deleted media is not counted");
    }

    [Fact]
    public async Task Handle_TourWithoutCategoryScheduleOrMedia_ReturnsNullsAndZero()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = PublicTourTestData.NewTour(1, "Bare tour");
        tour.SubmitForReview(T0);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        // Legacy rows can lack schedules even though the domain now forbids creating such a tour.
        db.TourSchedules.RemoveRange(db.TourSchedules.Where(s => s.TourId == tour.Id));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(new GetPendingToursQuery(), CancellationToken.None);

        var item = result.Value.Items.Should().ContainSingle().Subject;
        item.Category.Should().BeNull();
        item.NextDepartureAtUtc.Should().BeNull();
        item.NextDepartureCapacity.Should().BeNull();
        item.ImageCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_StillListsPendingTourOfLockedOperator()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1, accountStatus: AccountStatus.Locked);
        var tour = await AddTourAsync(db, 1, "Locked operator tour", TourStatus.Pending, T0);

        var result = await CreateHandler(db).Handle(new GetPendingToursQuery(), CancellationToken.None);

        result.Value.Items.Select(i => i.TourId).Should().Equal(tour.Id);
    }
}