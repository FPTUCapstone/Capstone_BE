using FluentAssertions;

using TripMate.Application.Features.Admin.Tours.Review;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using DomainTourMedia = TripMate.Domain.Entities.TourMedia;

namespace TripMate.Application.UnitTests.Features.Admin.Tours.Review;

/// <summary>
/// UC-60 Screen 13 additions to the TM-83 Staff review queue: keyword search and the columns the screen shows.
/// The original contract (id, operatorUserId, title, status, submittedAtUtc, submissionNote, paging) is unchanged.
/// </summary>
public sealed class GetTourReviewQueueSearchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
    private const long StaffId = 55;

    private static async Task<GetTourReviewQueueQueryHandler> CreateHandlerAsync(TestDbContext db)
    {
        db.Users.Add(new User
        {
            Id = StaffId,
            Email = "staff@test.local",
            FullName = "Staff",
            Role = UserRole.Staff,
            Status = AccountStatus.Active,
        });
        await db.SaveChangesAsync();
        return new GetTourReviewQueueQueryHandler(db, new FakeCurrentUserService { UserId = StaffId, Role = nameof(UserRole.Staff) });
    }

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

    [Fact]
    public async Task Handle_ListsOnlyPendingToursOldestSubmittedFirstThenById()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var newest = await AddTourAsync(db, 1, "Newest", TourStatus.Pending, T0.AddHours(5));
        var oldestA = await AddTourAsync(db, 1, "Oldest A", TourStatus.Pending, T0);
        var oldestB = await AddTourAsync(db, 1, "Oldest B", TourStatus.Pending, T0);
        await AddTourAsync(db, 1, "Draft", TourStatus.Draft, T0);
        await AddTourAsync(db, 1, "Approved", TourStatus.Approved, T0);
        await AddTourAsync(db, 1, "Rejected", TourStatus.Rejected, T0);
        var handler = await CreateHandlerAsync(db);

        var result = await handler.Handle(new GetTourReviewQueueQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");
        result.Value.Items.Select(i => i.Id).Should().Equal(oldestA.Id, oldestB.Id, newest.Id);
        result.Value.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Handle_FiltersByTitleOrOperatorCompanyName_IgnoringSurroundingSpaces()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1, company: "Sunrise Travel");
        await PublicTourTestData.SeedOperatorAsync(db, 2, company: "Lotus Journeys");
        var byTitle = await AddTourAsync(db, 1, "Hoi An lantern night", TourStatus.Pending, T0);
        var byCompany = await AddTourAsync(db, 2, "Mountain trek", TourStatus.Pending, T0.AddMinutes(1));
        await AddTourAsync(db, 1, "Unrelated cooking class", TourStatus.Pending, T0.AddMinutes(2));
        var handler = await CreateHandlerAsync(db);

        var title = await handler.Handle(new GetTourReviewQueueQuery(Keyword: "  lantern "), CancellationToken.None);
        var company = await handler.Handle(new GetTourReviewQueueQuery(Keyword: "lotus"), CancellationToken.None);

        title.Value.Items.Select(i => i.Id).Should().Equal(byTitle.Id);
        company.Value.Items.Select(i => i.Id).Should().Equal(byCompany.Id);
        title.Value.TotalCount.Should().Be(1);
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
        var handler = await CreateHandlerAsync(db);

        var result = await handler.Handle(new GetTourReviewQueueQuery(Keyword: keyword), CancellationToken.None);

        result.Value.Items.Should().BeEmpty("a wildcard typed by the reviewer must not match every pending tour");
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_PagesWithCorrectTotals_AndPastTheEndIsEmpty()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        for (var i = 0; i < 25; i++)
        {
            await AddTourAsync(db, 1, $"Tour {i:00}", TourStatus.Pending, T0.AddMinutes(i));
        }

        var handler = await CreateHandlerAsync(db);

        var page2 = await handler.Handle(new GetTourReviewQueueQuery(Page: 2, PageSize: 20), CancellationToken.None);
        var beyond = await handler.Handle(new GetTourReviewQueueQuery(Page: 9, PageSize: 20), CancellationToken.None);

        page2.Value.Items.Should().HaveCount(5);
        page2.Value.TotalCount.Should().Be(25);
        page2.Value.Page.Should().Be(2);
        page2.Value.Items[0].Title.Should().Be("Tour 20");
        beyond.Value.Items.Should().BeEmpty();
        beyond.Value.TotalCount.Should().Be(25);
    }

    [Fact]
    public async Task Handle_KeepsTheOriginalColumnsAndAddsTheScreenColumns()
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1, company: "Sunrise Travel");
        var departures = new (DateTimeOffset Start, int Total, decimal? Adult)[]
        {
            (T0.AddDays(30), 12, 2_000_000m),
            (T0.AddDays(10), 8, 1_500_000m),
        };
        var tour = PublicTourTestData.NewTour(1, "Mapped tour", departures);
        tour.SubmitForReview(T0, "Please review quickly");
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        db.TourMedia.AddRange(
            DomainTourMedia.Create(tour, "id/1", "https://res.cloudinary.com/x/1.webp", null, "alt 1", 1, true, T0),
            DomainTourMedia.Create(tour, "id/2", "https://res.cloudinary.com/x/2.webp", null, "alt 2", 2, false, T0));
        var deleted = DomainTourMedia.Create(tour, "id/3", "https://res.cloudinary.com/x/3.webp", null, "alt 3", 3, false, T0);
        deleted.SoftDelete(T0);
        db.TourMedia.Add(deleted);
        await db.SaveChangesAsync();
        var handler = await CreateHandlerAsync(db);

        var result = await handler.Handle(new GetTourReviewQueueQuery(), CancellationToken.None);

        var item = result.Value.Items.Should().ContainSingle().Subject;
        // Original TM-83 contract.
        item.Id.Should().Be(tour.Id);
        item.OperatorUserId.Should().Be(1);
        item.Title.Should().Be("Mapped tour");
        item.Status.Should().Be(nameof(TourStatus.Pending));
        item.SubmittedAtUtc.Should().Be(T0);
        item.SubmissionNote.Should().Be("Please review quickly");
        // Screen 13 additions.
        item.OperatorName.Should().Be("Sunrise Travel");
        item.Location.Should().Be("Hoi An, Da Nang");
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
        var tour = await AddTourAsync(db, 1, "Bare tour", TourStatus.Pending, T0);
        db.TourSchedules.RemoveRange(db.TourSchedules.Where(s => s.TourId == tour.Id));
        await db.SaveChangesAsync();
        var handler = await CreateHandlerAsync(db);

        var result = await handler.Handle(new GetTourReviewQueueQuery(), CancellationToken.None);

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
        var handler = await CreateHandlerAsync(db);

        var result = await handler.Handle(new GetTourReviewQueueQuery(), CancellationToken.None);

        result.Value.Items.Select(i => i.Id).Should().Equal(tour.Id);
    }

    [Fact]
    public void Validator_RejectsAKeywordLongerThan100Characters_AndKeepsTheOriginalPagingRules()
    {
        var validator = new GetTourReviewQueueQueryValidator();

        validator.Validate(new GetTourReviewQueueQuery()).IsValid.Should().BeTrue();
        validator.Validate(new GetTourReviewQueueQuery(Keyword: new string('a', 100))).IsValid.Should().BeTrue();
        validator.Validate(new GetTourReviewQueueQuery(Keyword: new string('a', 101))).IsValid.Should().BeFalse();
        validator.Validate(new GetTourReviewQueueQuery(Page: 0)).IsValid.Should().BeFalse();
        validator.Validate(new GetTourReviewQueueQuery(PageSize: 101)).IsValid.Should().BeFalse();
    }
}