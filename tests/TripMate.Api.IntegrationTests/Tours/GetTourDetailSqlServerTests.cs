using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tours;

/// <summary>
/// UC-26 against SQL Server: the visibility matrix, big 64-bit ids, schedule/media filtering, the rating
/// aggregate, a constant query count, and parity with UC-24 search (the two public predicates must agree).
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class GetTourDetailSqlServerTests
{
    private static string Stamp(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Detail_FollowsThePublicVisibilityMatrix()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operators = await SeedOperatorsAsync(database);
        var now = DateTime.UtcNow;
        var expected = new Dictionary<long, bool>
        {
            [await InsertTourAsync(database, operators.Approved, "Published", now.AddDays(-1))] = true,
            [await InsertTourAsync(database, operators.Approved, "Approved", now.AddDays(-1))] = true,
            [await InsertTourAsync(database, operators.Approved, "Approved", now.AddDays(1))] = false,
            [await InsertTourAsync(database, operators.Approved, "Approved", null)] = false,
            [await InsertTourAsync(database, operators.Approved, "Draft", null)] = false,
            [await InsertTourAsync(database, operators.Approved, "Pending", null)] = false,
            [await InsertTourAsync(database, operators.Approved, "Rejected", null)] = false,
            [await InsertTourAsync(database, operators.Approved, "Inactive", now.AddDays(-1))] = false,
            [await InsertTourAsync(database, operators.PendingOperator, "Published", now.AddDays(-1))] = false,
            [await InsertTourAsync(database, operators.RejectedOperator, "Published", now.AddDays(-1))] = false,
            [await InsertTourAsync(database, operators.LockedOperator, "Published", now.AddDays(-1))] = false,
        };
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateClient();

        foreach (var (tourId, visible) in expected)
        {
            using var response = await client.GetAsync($"/api/v1/tours/{tourId}");

            response.StatusCode.Should().Be(
                visible ? HttpStatusCode.OK : HttpStatusCode.NotFound, $"tour {tourId}");
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Detail_ReturnsLargeSignedInt64IdsAsStrings()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operators = await SeedOperatorsAsync(database);
        const long bigId = 9007199254740993;
        await database.ExecuteNonQueryAsync($"""
            SET IDENTITY_INSERT commerce.Tours ON;
            INSERT commerce.Tours(tour_id,operator_user_id,title,description,base_price,duration_days,status,published_at)
            VALUES({bigId},{operators.Approved},N'Big id tour',N'Description',1000000,1,'Published',SYSUTCDATETIME());
            SET IDENTITY_INSERT commerce.Tours OFF;
            """);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tours/{bigId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("tourId").ValueKind.Should().Be(JsonValueKind.String);
        body.RootElement.GetProperty("tourId").GetString().Should().Be("9007199254740993");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Detail_ListsFutureScheduledDeparturesAndAppliesLegacyPriceFallback()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operators = await SeedOperatorsAsync(database);
        var tourId = await InsertTourAsync(database, operators.Approved, "Published", DateTime.UtcNow.AddDays(-1));
        var now = DateTime.UtcNow;
        await database.ExecuteNonQueryAsync($"""
            INSERT commerce.TourSchedules(tour_id,start_datetime,end_datetime,total_capacity,reserved_capacity,status,adult_price,child_price)
            VALUES
              ({tourId},'{Stamp(now.AddDays(30))}','{Stamp(now.AddDays(31))}',10,4,'Scheduled',1200000,600000),
              ({tourId},'{Stamp(now.AddDays(10))}','{Stamp(now.AddDays(11))}',10,10,'Scheduled',1100000,NULL),
              ({tourId},'{Stamp(now.AddDays(20))}','{Stamp(now.AddDays(21))}',8,0,'Cancelled',1100000,NULL),
              ({tourId},'{Stamp(now.AddDays(-9))}','{Stamp(now.AddDays(-8))}',8,2,'Completed',1100000,NULL),
              ({tourId},'{Stamp(now.AddDays(-2))}','{Stamp(now.AddDays(-1))}',8,2,'Scheduled',1100000,NULL);
            INSERT commerce.TourSchedules(tour_id,start_datetime,end_datetime,total_capacity)
            VALUES({tourId},'{Stamp(now.AddDays(15))}','{Stamp(now.AddDays(16))}',5);
            """);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tours/{tourId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schedules = body.RootElement.GetProperty("schedules").EnumerateArray().ToList();
        schedules.Select(item => item.GetProperty("availabilityStatus").GetString())
            .Should().Equal("soldOut", "available", "available");
        schedules.Select(item => item.GetProperty("adultPrice").GetInt64())
            .Should().Equal(1_100_000, 1_000_000, 1_200_000);
        schedules.Select(item => item.GetProperty("remainingSlots").GetInt32()).Should().Equal(0, 5, 6);
        schedules.Select(item => item.GetProperty("bookable").GetBoolean()).Should().Equal(false, true, true);
        body.RootElement.GetProperty("hasBookableSchedule").GetBoolean().Should().BeTrue();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Detail_ReturnsOnlyActiveMediaWithThePrimaryFirst()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operators = await SeedOperatorsAsync(database);
        var tourId = await InsertTourAsync(database, operators.Approved, "Published", DateTime.UtcNow.AddDays(-1));
        var stamp = Stamp(DateTime.UtcNow);
        await database.ExecuteNonQueryAsync($"""
            INSERT commerce.TourMedia(tour_id,cloudinary_public_id,delivery_url,caption,sort_order,is_primary,lifecycle_status,created_at,updated_at,deleted_at,alt_text)
            VALUES
              ({tourId},N'tour/second',N'https://cdn.example.test/second.jpg',NULL,1,0,'Active','{stamp}','{stamp}',NULL,N'Second'),
              ({tourId},N'tour/primary',N'https://cdn.example.test/primary.jpg',N'Cover',2,1,'Active','{stamp}','{stamp}',NULL,N'Primary'),
              ({tourId},N'tour/gone',N'https://cdn.example.test/gone.jpg',NULL,3,0,'Deleted','{stamp}','{stamp}','{stamp}',N'Gone');
            """);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tours/{tourId}");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("images").EnumerateArray()
            .Select(image => image.GetProperty("url").GetString())
            .Should().Equal("https://cdn.example.test/primary.jpg", "https://cdn.example.test/second.jpg");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Detail_ReturnsTheAggregateOfPublishedReviews()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operators = await SeedOperatorsAsync(database);
        var rated = await InsertTourAsync(database, operators.Approved, "Published", DateTime.UtcNow.AddDays(-1));
        var unrated = await InsertTourAsync(database, operators.Approved, "Published", DateTime.UtcNow.AddDays(-1));
        await database.ExecuteNonQueryAsync($"""
            INSERT dbo.Users(role,status,email,full_name)
            VALUES('Traveler','Active',N'uc26-rating@test.invalid',N'Rating Traveler');
            DECLARE @traveler BIGINT = SCOPE_IDENTITY();
            INSERT planning.Itineraries(traveler_user_id,source_type,title) VALUES(@traveler,'Manual',N'UC-26 fixture');
            DECLARE @itinerary BIGINT = SCOPE_IDENTITY();
            INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
            VALUES('UC26-1',@traveler,@itinerary,0,0,'Completed'),('UC26-2',@traveler,@itinerary,0,0,'Completed');
            INSERT social.TripReviews(
                booking_id,traveler_user_id,tour_id,overall_rating,title,content,
                publish_display_name,public_display_name,publication_status,created_at,edit_deadline,updated_at)
            SELECT booking_id,@traveler,{rated},CASE booking_code WHEN 'UC26-1' THEN 4 ELSE 5 END,
                   N'Published',N'Published content',0,N'Traveler','Published',
                   SYSUTCDATETIME(),DATEADD(DAY,7,SYSUTCDATETIME()),SYSUTCDATETIME()
            FROM commerce.Bookings WHERE booking_code IN ('UC26-1','UC26-2');
            """);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateClient();

        using var ratedResponse = await client.GetAsync($"/api/v1/tours/{rated}");
        using var unratedResponse = await client.GetAsync($"/api/v1/tours/{unrated}");

        using var ratedBody = JsonDocument.Parse(await ratedResponse.Content.ReadAsStringAsync());
        var rating = ratedBody.RootElement.GetProperty("rating");
        rating.GetProperty("count").GetInt32().Should().Be(2);
        rating.GetProperty("average").GetDecimal().Should().Be(4.5m);
        using var unratedBody = JsonDocument.Parse(await unratedResponse.Content.ReadAsStringAsync());
        unratedBody.RootElement.GetProperty("rating").GetProperty("count").GetInt32().Should().Be(0);
        unratedBody.RootElement.GetProperty("rating").GetProperty("average").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Detail_ReturnsDestinationsInOrderAndItineraryStopsOfADeactivatedPoi()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operators = await SeedOperatorsAsync(database);
        var tourId = await InsertTourAsync(database, operators.Approved, "Published", DateTime.UtcNow.AddDays(-1));
        await database.ExecuteNonQueryAsync($"""
            INSERT catalog.Destinations(name) VALUES(N'Hội An'),(N'Đà Nẵng');
            INSERT commerce.TourDestinations(tour_id,destination_id,sequence_no)
            SELECT {tourId},destination_id,CASE name WHEN N'Đà Nẵng' THEN 1 ELSE 2 END FROM catalog.Destinations;
            """);
        long activePoi;
        long inactivePoi;
        await using (var context = database.CreateDbContext())
        {
            var category = PoiCategory.Create("UC-26 attraction", null);
            context.PoiCategories.Add(category);
            var active = PointOfInterest.Create(category, "Phố cổ Hội An", 15.88m, 108.33m, operators.Approved, DateTimeOffset.UtcNow);
            var inactive = PointOfInterest.Create(category, "Chùa đã đóng", 16.05m, 108.22m, operators.Approved, DateTimeOffset.UtcNow);
            context.PointsOfInterest.AddRange(active, inactive);
            await context.SaveChangesAsync();
            activePoi = active.Id;
            inactivePoi = inactive.Id;
        }

        await database.ExecuteNonQueryAsync($"""
            UPDATE catalog.POIs SET status = 'Inactive' WHERE poi_id = {inactivePoi};
            INSERT commerce.TourItineraryItems(tour_id,day_no,sequence_no,poi_id,planned_time,stay_duration_minutes,notes)
            VALUES({tourId},1,2,{activePoi},'09:15',60,N'Ăn sáng'),({tourId},1,1,{inactivePoi},NULL,45,NULL);
            """);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tours/{tourId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("destinations").EnumerateArray().Select(item => item.GetString())
            .Should().Equal("Đà Nẵng", "Hội An");
        var items = body.RootElement.GetProperty("itinerary")[0].GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("poi").GetProperty("name").GetString())
            .Should().Equal("Chùa đã đóng", "Phố cổ Hội An");
        items[1].GetProperty("plannedTime").GetString().Should().Be("09:15");
        items[1].GetProperty("poi").GetProperty("poiId").GetString().Should().Be(activePoi.ToString());
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Detail_QueryCountDoesNotGrowWithDeparturesStopsOrImages()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operators = await SeedOperatorsAsync(database);
        var small = await InsertTourAsync(database, operators.Approved, "Published", DateTime.UtcNow.AddDays(-1));
        var large = await InsertTourAsync(database, operators.Approved, "Published", DateTime.UtcNow.AddDays(-1));
        long poiId;
        await using (var context = database.CreateDbContext())
        {
            var category = PoiCategory.Create("UC-26 count", null);
            context.PoiCategories.Add(category);
            var poi = PointOfInterest.Create(category, "Count POI", 15.88m, 108.33m, operators.Approved, DateTimeOffset.UtcNow);
            context.PointsOfInterest.Add(poi);
            await context.SaveChangesAsync();
            poiId = poi.Id;
        }

        var now = DateTime.UtcNow;
        var stamp = Stamp(now);
        await database.ExecuteNonQueryAsync(AddChildren(small, 1, 1, 1, poiId, now, stamp));
        await database.ExecuteNonQueryAsync(AddChildren(large, 12, 12, 12, poiId, now, stamp));
        var counter = new CommandCounter();
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString, dbInterceptor: counter);
        using var client = factory.CreateClient();
        using var warmUp = await client.GetAsync($"/api/v1/tours/{small}");

        counter.Reset();
        using var smallResponse = await client.GetAsync($"/api/v1/tours/{small}");
        var smallCount = counter.Count;
        counter.Reset();
        using var largeResponse = await client.GetAsync($"/api/v1/tours/{large}");
        var largeCount = counter.Count;

        smallResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        largeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        smallCount.Should().BeGreaterThan(0);
        largeCount.Should().Be(smallCount, "the number of queries must not depend on the number of children");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Detail_AgreesWithUc24SearchOnWhichToursArePublic()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operators = await SeedOperatorsAsync(database);
        var now = DateTime.UtcNow;
        var ids = new List<long>
        {
            await InsertTourAsync(database, operators.Approved, "Published", now.AddDays(-1)),
            await InsertTourAsync(database, operators.Approved, "Approved", now.AddDays(-1)),
            await InsertTourAsync(database, operators.Approved, "Approved", now.AddDays(1)),
            await InsertTourAsync(database, operators.Approved, "Approved", null),
            await InsertTourAsync(database, operators.Approved, "Draft", null),
            await InsertTourAsync(database, operators.Approved, "Pending", null),
            await InsertTourAsync(database, operators.Approved, "Rejected", null),
            await InsertTourAsync(database, operators.Approved, "Inactive", now.AddDays(-1)),
            await InsertTourAsync(database, operators.PendingOperator, "Published", now.AddDays(-1)),
            await InsertTourAsync(database, operators.RejectedOperator, "Published", now.AddDays(-1)),
            await InsertTourAsync(database, operators.LockedOperator, "Published", now.AddDays(-1)),
        };
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateClient();

        using var search = await client.GetAsync("/api/v1/tours?pageSize=50");
        using var searchBody = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var searchIds = searchBody.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => long.Parse(item.GetProperty("tourId").GetString()!, CultureInfo.InvariantCulture))
            .Order().ToList();
        var detailIds = new List<long>();
        foreach (var id in ids)
        {
            using var detail = await client.GetAsync($"/api/v1/tours/{id}");
            if (detail.StatusCode == HttpStatusCode.OK) detailIds.Add(id);
        }

        search.StatusCode.Should().Be(HttpStatusCode.OK);
        detailIds.Order().Should().Equal(searchIds);
        searchIds.Should().NotBeEmpty();
    }

    private static string AddChildren(long tourId, int departures, int stops, int images, long poiId, DateTime now, string stamp)
    {
        var sql = new System.Text.StringBuilder();
        for (var index = 0; index < departures; index++)
        {
            sql.AppendLine($"INSERT commerce.TourSchedules(tour_id,start_datetime,end_datetime,total_capacity,adult_price) VALUES({tourId},'{Stamp(now.AddDays(10 + index))}','{Stamp(now.AddDays(11 + index))}',10,1000000);");
        }

        for (var index = 0; index < stops; index++)
        {
            sql.AppendLine($"INSERT commerce.TourItineraryItems(tour_id,day_no,sequence_no,poi_id,stay_duration_minutes) VALUES({tourId},1,{index + 1},{poiId},30);");
        }

        for (var index = 0; index < images; index++)
        {
            sql.AppendLine($"INSERT commerce.TourMedia(tour_id,cloudinary_public_id,delivery_url,sort_order,is_primary,lifecycle_status,created_at,updated_at,alt_text) VALUES({tourId},N'tour/{tourId}-{index}',N'https://cdn.example.test/{tourId}-{index}.jpg',{index + 1},{(index == 0 ? 1 : 0)},'Active','{stamp}','{stamp}',N'Image {index}');");
        }

        return sql.ToString();
    }

    private static async Task<Operators> SeedOperatorsAsync(SqlServerTestDatabase database)
    {
        async Task<long> Insert(string name, string accountStatus, string approval) =>
            await database.ExecuteScalarAsync<long>($"""
                INSERT dbo.Users(role,status,email,full_name)
                VALUES('TourOperator','{accountStatus}',N'{name}@uc26.test',N'{name}');
                DECLARE @id BIGINT = SCOPE_IDENTITY();
                INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,contact_phone,contact_address,approval_status)
                VALUES(@id,N'{name} Tours',N'TAX-{name}',N'LIC-{name}',N'0900000001',N'Private address','{approval}');
                SELECT CONVERT(BIGINT,@id);
                """);

        return new Operators(
            await Insert("approved", "Active", "Approved"),
            await Insert("pending", "Active", "PendingApproval"),
            await Insert("rejected", "Active", "Rejected"),
            await Insert("locked", "Locked", "Approved"));
    }

    private static Task<long> InsertTourAsync(SqlServerTestDatabase database, long operatorId, string status, DateTime? publishedAt) =>
        database.ExecuteScalarAsync<long>($"""
            INSERT commerce.Tours(operator_user_id,title,description,base_price,duration_days,status,published_at)
            VALUES({operatorId},N'UC-26 {status} tour',N'Description',1000000,1,'{status}',{(publishedAt.HasValue ? $"'{Stamp(publishedAt.Value)}'" : "NULL")});
            SELECT CONVERT(BIGINT, SCOPE_IDENTITY());
            """);

    private sealed record Operators(long Approved, long PendingOperator, long RejectedOperator, long LockedOperator);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }
    }
}