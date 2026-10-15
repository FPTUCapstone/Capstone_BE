using System.Data.Common;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.GetCategories;
using TripMate.Application.Features.Tours.Search.V2;
using TripMate.Domain.Entities;

using Xunit.Abstractions;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourSearchV2SqlServerTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Independent_DefaultUsesSingleSnapshotEqualityAndNumericScheduleTieBreak()
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();
        var clock = new CountingClock();
        var reader = new TourSearchV2IndependentReader(context, clock);
        var page = await reader.ReadAsync(new TourSearchV2Request(), CancellationToken.None);
        clock.Calls.Should().Be(1);
        page.AsOfUtc.Should().Be(Now.UtcDateTime);
        page.TotalCount.Should().Be(4);
        page.Items.Select(item => item.TourId).Should().Equal("1", "3", "2", "4");
        page.Items[0].RepresentativeScheduleId.Should().Be("9");
        page.Items[0].DepartureAtUtc.Should().Be(Now.UtcDateTime);
        page.Items[0].AvailabilityStatus.Should().Be("soldOut");
        page.Items[0].RemainingSlots.Should().Be(0);
        page.Items[2].RepresentativeScheduleId.Should().BeNull();
        page.Items[2].AvailabilityStatus.Should().Be("noUpcomingSchedule");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Independent_InclusiveVietnamDateSelectsPastCompletedPreservingCapacity()
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();
        var reader = new TourSearchV2IndependentReader(context, new CountingClock());
        await database.ExecuteNonQueryAsync("""
            INSERT INTO catalog.Destinations (name) VALUES (N'Huế'), (N'Đà Nẵng');
            INSERT INTO commerce.TourDestinations (tour_id, destination_id, sequence_no)
            SELECT 1, destination_id, 1 FROM catalog.Destinations WHERE name = N'Huế';
            INSERT INTO commerce.TourDestinations (tour_id, destination_id, sequence_no)
            SELECT 1, destination_id, 2 FROM catalog.Destinations WHERE name = N'Đà Nẵng';
            """);
        (await reader.ReadAsync(new TourSearchV2Request(Destination: "  hue ", MinPrice: 500, MaxPrice: 500), CancellationToken.None))
            .Items.Select(item => item.TourId).Should().Equal("1");
        (await reader.ReadAsync(new TourSearchV2Request(Destination: "DA NANG"), CancellationToken.None))
            .Items.Select(item => item.TourId).Should().Equal("1");
        var page = await reader.ReadAsync(new TourSearchV2Request(
            StartDate: new DateOnly(2026, 10, 14), EndDate: new DateOnly(2026, 10, 14)), CancellationToken.None);
        page.TotalCount.Should().Be(2);
        page.Items.Select(item => item.TourId).Should().Equal("2", "1");
        page.Items[0].RepresentativeScheduleId.Should().Be("20");
        page.Items[0].AvailabilityStatus.Should().Be("available");
        page.Items[0].RemainingSlots.Should().Be(8);
        page.Items[0].DepartureAtUtc.Should().Be(new DateTime(2026, 10, 13, 17, 0, 0, DateTimeKind.Utc));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Independent_SingleBoundsDurationCategoryAndLegacyNulls()
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();
        var reader = new TourSearchV2IndependentReader(context, new CountingClock());
        (await reader.ReadAsync(new TourSearchV2Request(StartDate: new DateOnly(2026, 10, 15)), CancellationToken.None))
            .Items.Select(item => item.TourId).Should().Equal("1", "3");
        (await reader.ReadAsync(new TourSearchV2Request(EndDate: new DateOnly(2026, 10, 14)), CancellationToken.None))
            .Items.Select(item => item.TourId).Should().Equal("2", "1");
        (await reader.ReadAsync(new TourSearchV2Request(DurationDays: 2, Category: "ACTIVE"), CancellationToken.None))
            .Items.Select(item => item.TourId).Should().Equal("1");
        var validator = new TourSearchV2RequestValidator(context);
        (await validator.ValidateAsync(new TourSearchV2Request(Category: "ACTIVE"))).IsValid.Should().BeTrue();
        (await validator.ValidateAsync(new TourSearchV2Request(Category: "inactive"))).IsValid.Should().BeFalse();
        (await validator.ValidateAsync(new TourSearchV2Request(Category: "missing"))).IsValid.Should().BeFalse();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Independent_PriceSortsTitleIdTiesAndLargeValidEmptyPage()
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();
        var reader = new TourSearchV2IndependentReader(context, new CountingClock());
        (await reader.ReadAsync(new TourSearchV2Request(Sort: TourSearchV2Request.SortPriceAscending), CancellationToken.None))
            .Items.Select(item => item.TourId).Should().Equal("2", "3", "4", "1");
        (await reader.ReadAsync(new TourSearchV2Request(Sort: TourSearchV2Request.SortPriceDescending), CancellationToken.None))
            .Items.Select(item => item.TourId).Should().Equal("1", "3", "4", "2");
        var page = await reader.ReadAsync(new TourSearchV2Request(Page: int.MaxValue, PageSize: 100), CancellationToken.None);
        page.Page.Should().Be(int.MaxValue);
        page.TotalCount.Should().Be(4);
        page.TotalPages.Should().Be(1);
        page.Items.Should().BeEmpty();
        var extremeDates = await reader.ReadAsync(new TourSearchV2Request(
            StartDate: DateOnly.MinValue, EndDate: DateOnly.MaxValue), CancellationToken.None);
        extremeDates.TotalCount.Should().Be(3);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Independent_UnknownCapacityRetainsIdentityAndRealPrimaryThumbnail()
    {
        await using var database = await SeedAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE commerce.TourSchedules NOCHECK CONSTRAINT ALL;
            UPDATE commerce.TourSchedules SET reserved_capacity = 11 WHERE schedule_id = 9;
            INSERT INTO commerce.TourMedia (tour_id, cloudinary_public_id, delivery_url, sort_order, is_primary, lifecycle_status, alt_text)
            VALUES (1, N'fixture/primary', N'https://example.test/primary.jpg', 1, 1, 'Active', N'Fixture primary');
            """);
        await using var context = database.CreateDbContext();
        var page = await new TourSearchV2IndependentReader(context, new CountingClock())
            .ReadAsync(new TourSearchV2Request(), CancellationToken.None);
        var row = page.Items[0];
        row.AvailabilityStatus.Should().Be("unknown");
        row.RemainingSlots.Should().BeNull();
        row.RepresentativeScheduleId.Should().Be("9");
        row.DepartureAtUtc.Should().Be(Now.UtcDateTime);
        row.ThumbnailUrl.Should().Be("https://example.test/primary.jpg");
        row.ToContract(null).AggregateRating.Should().BeNull();
        row.ToContract(4.7m).AggregateRating.Should().Be(4.7m);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Independent_GuardsBlockedSemanticsAndTaxonomyUsesSqlOrdering()
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();
        var reader = new TourSearchV2IndependentReader(context, new CountingClock());
        foreach (var request in new[] { new TourSearchV2Request(Keyword: "Tour"),
            new TourSearchV2Request(MinimumRating: 4), new TourSearchV2Request(Sort: TourSearchV2Request.SortRating),
            new TourSearchV2Request(Sort: TourSearchV2Request.SortRelevance) })
        {
            Func<Task> read = async () => await reader.ReadAsync(request, CancellationToken.None);
            await read.Should().ThrowAsync<NotSupportedException>();
        }
        var categories = await new GetTourCategoriesQueryHandler(context).Handle(new GetTourCategoriesQuery(), CancellationToken.None);
        categories.Value.Select(category => category.Code).Should().Equal("active", "other");
        await database.ExecuteNonQueryAsync("""
            INSERT INTO catalog.TourCategories (code, name, is_active)
            VALUES ('z-fixture', N'Beach', 1), ('a-fixture', N'Beach', 1);
            """);
        categories = await new GetTourCategoriesQueryHandler(context).Handle(new GetTourCategoriesQuery(), CancellationToken.None);
        categories.Value.Select(category => category.Code).Should().Equal("active", "a-fixture", "other", "z-fixture");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RawOnly_R001_R002_R003_TransportsSharedSqlMeanAndUnratedNull()
    {
        await using var database = await SeedAsync();
        await SeedRawReviewsAsync(database);
        await using var context = database.CreateDbContext();
        var page = await new TourSearchV2IndependentReader(context, new CountingClock())
            .ReadAsync(new TourSearchV2Request(), CancellationToken.None);

        page.Items.Single(row => row.TourId == "1").RawAverage.Should().Be(4.666666m);
        page.Items.Single(row => row.TourId == "2").RawAverage.Should().Be(3m);
        page.Items.Single(row => row.TourId == "3").RawAverage.Should().BeNull();
        page.TotalCount.Should().Be(4);
        (await context.TripReviews.CountAsync()).Should().Be(1);
        (await context.Reviews.CountAsync()).Should().Be(7, "the reader must not mutate legacy rows");
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData(TourSearchV2Request.SortDepartureDate)]
    [InlineData(TourSearchV2Request.SortPriceAscending)]
    [InlineData(TourSearchV2Request.SortPriceDescending)]
    public async Task RawOnly_R004_ReviewValuesCannotChangeExistingMembershipOrderOrPages(string sort)
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();
        Task<IndependentTourSearchPage> Read(TourSearchV2Request criteria) =>
            new TourSearchV2IndependentReader(context, new CountingClock()).ReadAsync(criteria, CancellationToken.None);
        var request = new TourSearchV2Request(Sort: sort, PageSize: 2);
        var before = await Read(request);
        var beforeSecond = await Read(request with { Page = 2 });
        await SeedRawReviewsAsync(database);
        var after = await Read(request);
        var afterSecond = await Read(request with { Page = 2 });

        after.Items.Select(row => row.TourId).Should().Equal(before.Items.Select(row => row.TourId));
        afterSecond.Items.Select(row => row.TourId).Should().Equal(beforeSecond.Items.Select(row => row.TourId));
        after.TotalCount.Should().Be(before.TotalCount).And.Be(4);
        after.TotalPages.Should().Be(before.TotalPages).And.Be(2);
        afterSecond.TotalCount.Should().Be(4);
        afterSecond.TotalPages.Should().Be(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RawOnly_R005_R009_MapsOnlyVisiblePagesAndPreservesPrimaryThumbnail()
    {
        await using var database = await SeedAsync();
        await SeedRawReviewsAsync(database);
        await database.ExecuteNonQueryAsync("""
            INSERT commerce.TourMedia(tour_id,cloudinary_public_id,delivery_url,sort_order,is_primary,lifecycle_status,alt_text)
            VALUES(1,N'raw/primary',N'https://example.test/raw-primary.jpg',1,1,'Active',N'Raw fixture');
            """);
        await using var context = database.CreateDbContext();
        var reader = new TourSearchV2IndependentReader(context, new CountingClock());
        var first = await reader.ReadAsync(new TourSearchV2Request(PageSize: 2), CancellationToken.None);
        var second = await reader.ReadAsync(new TourSearchV2Request(Page: 2, PageSize: 2), CancellationToken.None);
        var beyond = await reader.ReadAsync(new TourSearchV2Request(Page: 3, PageSize: 2), CancellationToken.None);

        first.Items.Select(row => row.TourId).Should().Equal("1", "3");
        second.Items.Select(row => row.TourId).Should().Equal("2", "4");
        first.Items[0].RawAverage.Should().Be(4.666666m);
        first.Items[1].RawAverage.Should().BeNull();
        second.Items[0].RawAverage.Should().Be(3m);
        second.Items[1].RawAverage.Should().BeNull();
        first.Items[0].ThumbnailUrl.Should().Be("https://example.test/raw-primary.jpg");
        first.Items[0].RepresentativeScheduleId.Should().Be("9");
        beyond.Items.Should().BeEmpty();
        beyond.TotalCount.Should().Be(4);
        beyond.TotalPages.Should().Be(2);
        first.Items.Concat(second.Items).Select(row => row.TourId).Should().OnlyHaveUniqueItems();
        (await reader.ReadAsync(new TourSearchV2Request(StartDate: new DateOnly(2026, 10, 14),
            EndDate: new DateOnly(2026, 10, 14)), CancellationToken.None)).Items
            .Select(row => row.TourId).Should().Equal("2", "1");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RawOnly_R008_UsesFixedBatchedCommandsAndProviderAggregateSql()
    {
        await using var database = await SeedAsync();
        await SeedRawReviewsAsync(database);
        var counter = new TestCommandCounterInterceptor();
        await using var context = database.CreateDbContext(counter);
        var reader = new TourSearchV2IndependentReader(context, new CountingClock());
        var small = await reader.ReadAsync(new TourSearchV2Request(PageSize: 1), CancellationToken.None);
        small.Items.Should().ContainSingle();
        counter.CommandCount.Should().Be(4);
        var pageSql = counter.CommandTexts.Single(sql => sql.Contains("AVG("));
        pageSql.Should().Contain("UNION ALL").And.Contain("LEFT JOIN").And.Contain("OFFSET").And.Contain("FETCH");
        pageSql.Should().NotContain("ROUND(");
        output.WriteLine("Raw-only SQL count:\n{0}", counter.CommandTexts.First());
        output.WriteLine("Raw-only SQL page:\n{0}", pageSql);
        await database.ExecuteNonQueryAsync("""
            SET IDENTITY_INSERT commerce.Tours ON;
            INSERT commerce.Tours(tour_id,operator_user_id,title,base_price,duration_days,status,published_at)
            VALUES(8,901,N'Added one',300,2,'Approved','2026-10-01'),
                  (9,901,N'Added two',400,2,'Approved','2026-10-01');
            SET IDENTITY_INSERT commerce.Tours OFF;
            INSERT social.Reviews(traveler_user_id,target_type,target_id,rating)
            VALUES(902,'Tour',8,2),(902,'Tour',9,4);
            """);
        counter.Reset();
        var larger = await reader.ReadAsync(new TourSearchV2Request(PageSize: 100), CancellationToken.None);
        larger.TotalCount.Should().Be(6);
        larger.Items.Should().HaveCount(6);
        larger.Items.Single(row => row.TourId == "8").RawAverage.Should().Be(2m);
        larger.Items.Single(row => row.TourId == "9").RawAverage.Should().Be(4m);
        counter.CommandCount.Should().Be(4, "raw aggregation must not add a per-Tour read");
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RawOnly_R006_R010_KeepsPolicyGuardsClockAndCancellation()
    {
        await using var database = await SeedAsync();
        await SeedRawReviewsAsync(database);
        await using var context = database.CreateDbContext();
        var clock = new CountingClock();
        var reader = new TourSearchV2IndependentReader(context, clock);
        foreach (var request in new[] { new TourSearchV2Request(MinimumRating: 4.6m),
            new TourSearchV2Request(Sort: TourSearchV2Request.SortRating), new TourSearchV2Request(Keyword: "First"),
            new TourSearchV2Request(Sort: TourSearchV2Request.SortRelevance) })
        {
            var guarded = async () => await reader.ReadAsync(request, CancellationToken.None);
            await guarded.Should().ThrowAsync<NotSupportedException>();
        }
        clock.Calls.Should().Be(0);
        var page = await reader.ReadAsync(new TourSearchV2Request(), CancellationToken.None);
        clock.Calls.Should().Be(1);
        page.AsOfUtc.Should().Be(Now.UtcDateTime);
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        var cancelled = async () => await reader.ReadAsync(new TourSearchV2Request(), preCancelled.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        using var duringPage = new CancellationTokenSource();
        var interceptor = new CancelRawPageInterceptor(duringPage);
        await using var cancellingContext = database.CreateDbContext(interceptor);
        var during = async () => await new TourSearchV2IndependentReader(cancellingContext, new CountingClock())
            .ReadAsync(new TourSearchV2Request(), duringPage.Token);
        await during.Should().ThrowAsync<OperationCanceledException>();
        interceptor.PageCancellationObserved.Should().BeTrue();
    }

    private sealed class CancelRawPageInterceptor(CancellationTokenSource source) : DbCommandInterceptor
    {
        public bool PageCancellationObserved { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("AVG(", StringComparison.Ordinal))
            {
                source.Cancel();
                PageCancellationObserved = cancellationToken.IsCancellationRequested;
                cancellationToken.ThrowIfCancellationRequested();
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task SeedRawReviewsAsync(SqlServerTestDatabase database)
    {
        await database.ExecuteNonQueryAsync("""
            SET IDENTITY_INSERT dbo.Users ON;
            INSERT dbo.Users(user_id,role,status,email,full_name)
            VALUES(902,'Traveler','Active',N'raw-only@example.test',N'Raw owner');
            SET IDENTITY_INSERT dbo.Users OFF;
            INSERT planning.Itineraries(traveler_user_id,source_type,title)
            VALUES(902,'Manual',N'Raw one'),(902,'Manual',N'Raw two');
            INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
            VALUES('RAW-1',902,1,0,0,'Completed'),('RAW-2',902,2,0,0,'Completed');
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(902,'Tour',1,1,1),(902,'Tour',1,2,4),(902,'Tour',1,NULL,5),
                  (902,'Tour',2,NULL,3),(902,'Tour',2,NULL,3),
                  (902,'Tour',5,NULL,1),(902,'POI',1,NULL,1);
            """);
        await using var context = database.CreateDbContext();
        context.TripReviews.Add(TripReview.CreatePublished(1, 902, 1, null, 5,
            "Raw title", "Raw content", null, null, false, "Raw owner", null, Now));
        await context.SaveChangesAsync();
    }

    private static async Task<SqlServerTestDatabase> SeedAsync()
    {
        var database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.ExecuteNonQueryAsync("""
                INSERT INTO catalog.TourCategories (code, name, is_active)
                VALUES ('active', N'Adventure', 1), ('inactive', N'A hidden fixture', 0), ('other', N'Beach', 1);
                SET IDENTITY_INSERT dbo.Users ON;
                INSERT INTO dbo.Users (user_id, role, email, full_name, status)
                VALUES (901, 'TourOperator', N'v2-fixture@example.test', N'Fixture operator', 'Active');
                SET IDENTITY_INSERT dbo.Users OFF;
                INSERT INTO dbo.OperatorProfiles (user_id, company_name, tax_code, business_license_no, approval_status)
                VALUES (901, N'Fixture Travel', N'V2-901', N'V2-LIC', 'Approved');
                SET IDENTITY_INSERT commerce.Tours ON;
                INSERT INTO commerce.Tours (tour_id, operator_user_id, title, base_price, duration_days, status, published_at)
                VALUES (1, 901, N'First', 500, 2, 'Approved', '2026-10-01'),
                    (2, 901, N'Second', 100, 3, 'Approved', '2026-10-01'),
                    (3, 901, N'Tied', 200, 2, 'Approved', '2026-10-01'),
                    (4, 901, N'Tied', 200, 2, 'Approved', '2026-10-01'),
                    (5, 901, N'Draft', 1, 2, 'Draft', '2026-10-01'),
                    (6, 901, N'Future', 1, 2, 'Approved', '2026-10-16'),
                    (7, 901, N'Unpublished', 1, 2, 'Approved', NULL);
                SET IDENTITY_INSERT commerce.Tours OFF;
                UPDATE commerce.Tours SET category_id = (SELECT category_id FROM catalog.TourCategories WHERE code='active') WHERE tour_id=1;
                UPDATE commerce.Tours SET category_id = (SELECT category_id FROM catalog.TourCategories WHERE code='inactive') WHERE tour_id=3;
                SET IDENTITY_INSERT commerce.TourSchedules ON;
                INSERT INTO commerce.TourSchedules (schedule_id, tour_id, start_datetime, end_datetime, total_capacity, reserved_capacity, status)
                VALUES (10, 1, '2026-10-15T00:00:00', '2026-10-16T00:00:00', 10, 0, 'Scheduled'),
                    (9, 1, '2026-10-15T00:00:00', '2026-10-16T00:00:00', 10, 10, 'Scheduled'),
                    (8, 1, '2026-10-14T23:59:59', '2026-10-16T00:00:00', 10, 0, 'Scheduled'),
                    (7, 1, '2026-10-13T17:00:00', '2026-10-14T00:00:00', 10, 0, 'Cancelled'),
                    (6, 1, '2026-10-14T16:59:59', '2026-10-15T00:00:00', 10, 1, 'Completed'),
                    (20, 2, '2026-10-13T17:00:00', '2026-10-14T00:00:00', 10, 2, 'Completed'),
                    (30, 3, '2026-10-15T17:00:00', '2026-10-16T17:00:00', 10, 0, 'Completed'),
                    (40, 4, '2026-10-16T00:00:00', '2026-10-17T00:00:00', 10, 0, 'Cancelled');
                SET IDENTITY_INSERT commerce.TourSchedules OFF;
                """);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private sealed class CountingClock : IDateTimeProvider
    {
        public int Calls { get; private set; }
        public DateTimeOffset UtcNow { get { Calls++; return Now.AddSeconds(Calls - 1); } }
    }
}