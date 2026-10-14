using System.Xml.Linq;

using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

using Xunit.Abstractions;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourRatingFeasibilitySqlServerTests(ITestOutputHelper output)
{
    private const string MigrationFileName = "20261014_add_trip_review_rating_index.sql";
    private const string RatingIndexName = "IX_TripReviews_TourPublicationRating";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_HasCoveringTourPublicationRatingIndex()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        await AssertIndexShapeAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenIndexIsMissing_AddsItAndCanBeRerun()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(
            $"DROP INDEX {RatingIndexName} ON social.TripReviews;");

        await ApplyMigrationAsync(database);
        await ApplyMigrationAsync(database);

        await AssertIndexShapeAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenSameNameIndexHasWrongShape_RollsBack()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync($"""
            DROP INDEX {RatingIndexName} ON social.TripReviews;
            CREATE INDEX {RatingIndexName}
                ON social.TripReviews(publication_status, tour_id);
            """);

        var action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*incompatible shape*");
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*)
            FROM sys.index_columns AS ic
            INNER JOIN sys.indexes AS i
                ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'social.TripReviews')
              AND i.name = N'{RatingIndexName}'
              AND ic.key_ordinal = 1
              AND c.name = N'publication_status';
            """)).Should().Be(1, "the migration must not rewrite a conflicting index");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ApprovedPublishedOnlyAggregate_ExcludesLegacyUnknownPoiAndOtherTourRows()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedBehaviorFixtureAsync(database);

        var aggregate = await ReadAggregateAsync(database, tourId: 1);
        var unrated = await ReadAggregateAsync(database, tourId: 3);

        aggregate.Count.Should().Be(3);
        aggregate.Average.Should().BeApproximately(14m / 3m, 0.000001m);
        unrated.Should().Be(new RatingAggregate(null, 0));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RatingFilterUsesUnroundedMeanAndSortPlacesUnratedToursLast()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedBehaviorFixtureAsync(database);

        (await ReadFilteredTourIdsAsync(database, minimumRating: 4.6m))
            .Should().Equal(2, 1);
        (await ReadFilteredTourIdsAsync(database, minimumRating: 4.7m))
            .Should().Equal(2);
        (await ReadFilteredTourIdsAsync(database, minimumRating: null))
            .Should().Equal(2, 1, 3);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PageBoundedSetQuery_UsesCoveringIndexAndReducesLogicalReads()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedPlanFixtureAsync(database);

        var indexed = await ReadPageWithStatisticsAsync(database);
        await database.ExecuteNonQueryAsync(
            $"DROP INDEX {RatingIndexName} ON social.TripReviews;");
        var baseline = await ReadPageWithStatisticsAsync(database);
        await ApplyMigrationAsync(database);

        indexed.PlanXml.Should().Contain(RatingIndexName);
        indexed.ResultCount.Should().Be(20);
        baseline.ResultCount.Should().Be(20);
        indexed.TripReviewLogicalReads.Should().BeLessThan(
            baseline.TripReviewLogicalReads,
            "the measured covering index must reduce reads for the set-based rating page query");
        output.WriteLine(
            "TripReviews logical reads: indexed={0}, baseline={1}; result rows={2}.",
            indexed.TripReviewLogicalReads,
            baseline.TripReviewLogicalReads,
            indexed.ResultCount);
        await AssertIndexShapeAsync(database);
    }

    private static async Task SeedBehaviorFixtureAsync(SqlServerTestDatabase database)
    {
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role,status,email,full_name)
            VALUES('Traveler','Active',N'tm70-rating@test.invalid',N'Rating Traveler'),
                  ('TourOperator','Active',N'tm70-rating-operator@test.invalid',N'Rating Operator');
            INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,approval_status)
            VALUES(2,N'Rating Operator',N'TM70-RATING-TAX',N'TM70-RATING-LICENSE','Approved');
            INSERT commerce.Tours(operator_user_id,title,base_price,duration_days,status,published_at)
            VALUES(2,N'Boundary Tour',100000,1,'Approved','2026-10-01T00:00:00'),
                  (2,N'Highest Tour',100000,1,'Approved','2026-10-01T00:00:00'),
                  (2,N'Unrated Tour',100000,1,'Approved','2026-10-01T00:00:00');
            INSERT planning.Itineraries(traveler_user_id,source_type,title)
            VALUES(1,'Manual',N'Rating Fixture');
            INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
            VALUES('TM70-RATING-1',1,1,0,0,'Completed'),
                  ('TM70-RATING-2',1,1,0,0,'Completed'),
                  ('TM70-RATING-3',1,1,0,0,'Completed'),
                  ('TM70-RATING-4',1,1,0,0,'Completed');
            INSERT social.TripReviews(
                booking_id,traveler_user_id,tour_id,overall_rating,title,content,
                publish_display_name,public_display_name,publication_status,
                created_at,edit_deadline,updated_at)
            VALUES
                (1,1,1,4,N'Published 1',N'Published content',0,N'Traveler','Published','2026-10-01','2026-10-08','2026-10-01'),
                (2,1,1,5,N'Published 2',N'Published content',0,N'Traveler','Published','2026-10-01','2026-10-08','2026-10-01'),
                (3,1,1,5,N'Published 3',N'Published content',0,N'Traveler','Published','2026-10-01','2026-10-08','2026-10-01'),
                (4,1,2,5,N'Published 4',N'Published content',0,N'Traveler','Published','2026-10-01','2026-10-08','2026-10-01');

            -- social.Reviews has no publication provenance. These rows model
            -- legacy publication-unknown data and must not enter the approved query.
            INSERT social.Reviews(traveler_user_id,target_type,target_id,rating)
            VALUES(1,'Tour',1,1),(1,'POI',1,1),(1,'Tour',2,1),(1,'Tour',99,5);
            """);
    }

    private static async Task SeedPlanFixtureAsync(SqlServerTestDatabase database)
    {
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role,status,email,full_name)
            VALUES('Traveler','Active',N'tm70-plan@test.invalid',N'Plan Traveler'),
                  ('TourOperator','Active',N'tm70-plan-operator@test.invalid',N'Plan Operator');
            INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,approval_status)
            VALUES(2,N'Plan Operator',N'TM70-PLAN-TAX',N'TM70-PLAN-LICENSE','Approved');
            INSERT planning.Itineraries(traveler_user_id,source_type,title)
            VALUES(1,'Manual',N'Plan Fixture');

            ;WITH n AS (
                SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS value
                FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b)
            INSERT commerce.Tours(operator_user_id,title,base_price,duration_days,status,published_at)
            SELECT 2, CONCAT(N'Plan Tour ', value), 100000, 1, 'Approved', '2026-10-01T00:00:00'
            FROM n;

            ;WITH n AS (
                SELECT TOP (4000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS value
                FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b)
            INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
            SELECT CONCAT('TM70-PLAN-', value), 1, 1, 0, 0, 'Completed'
            FROM n;

            ;WITH source AS (
                SELECT booking_id,
                       ROW_NUMBER() OVER (ORDER BY booking_id) AS value
                FROM commerce.Bookings)
            INSERT social.TripReviews(
                booking_id,traveler_user_id,tour_id,overall_rating,title,content,
                publish_display_name,public_display_name,publication_status,
                created_at,edit_deadline,updated_at)
            SELECT booking_id, 1, ((value - 1) % 200) + 1, ((value - 1) % 5) + 1,
                   N'Plan rating', REPLICATE(N'Covering index evidence ', 20),
                   0, N'Traveler', 'Published',
                   '2026-10-01', '2026-10-08', '2026-10-01'
            FROM source;
            """);
    }

    private static async Task<RatingAggregate> ReadAggregateAsync(
        SqlServerTestDatabase database,
        long tourId)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AVG(CAST(overall_rating AS DECIMAL(10,4))), COUNT_BIG(*)
            FROM social.TripReviews
            WHERE tour_id = @tourId AND publication_status = 'Published';
            """;
        command.Parameters.AddWithValue("@tourId", tourId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new RatingAggregate(
            reader.IsDBNull(0) ? null : reader.GetDecimal(0),
            reader.GetInt64(1));
    }

    private static async Task<IReadOnlyList<long>> ReadFilteredTourIdsAsync(
        SqlServerTestDatabase database,
        decimal? minimumRating)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH rating AS (
                SELECT tour_id, AVG(CAST(overall_rating AS DECIMAL(10,4))) AS average_rating
                FROM social.TripReviews
                WHERE tour_id IS NOT NULL AND publication_status = 'Published'
                GROUP BY tour_id)
            SELECT tour.tour_id
            FROM commerce.Tours AS tour
            LEFT JOIN rating ON rating.tour_id = tour.tour_id
            WHERE @minimumRating IS NULL OR rating.average_rating >= @minimumRating
            ORDER BY CASE WHEN rating.average_rating IS NULL THEN 1 ELSE 0 END,
                     rating.average_rating DESC, tour.title ASC, tour.tour_id ASC;
            """;
        command.Parameters.Add(new SqlParameter("@minimumRating", System.Data.SqlDbType.Decimal)
        {
            Precision = 2,
            Scale = 1,
            Value = minimumRating ?? (object)DBNull.Value,
        });
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static async Task<PageStatistics> ReadPageWithStatisticsAsync(
        SqlServerTestDatabase database)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, "SET STATISTICS XML ON;");
        var count = 0;
        string? planXml = null;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = PageQuery;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) count++;
            while (await reader.NextResultAsync())
            {
                if (reader.FieldCount != 1 || !await reader.ReadAsync()) continue;
                var candidate = reader.GetValue(0)?.ToString();
                if (candidate?.Contains("ShowPlanXML", StringComparison.Ordinal) == true)
                    planXml = candidate;
            }
        }
        finally
        {
            await ExecuteNonQueryAsync(connection, "SET STATISTICS XML OFF;");
        }

        planXml.Should().NotBeNull("SQL Server must return an actual execution plan");
        var plan = XDocument.Parse(planXml!);
        var reads = plan.Descendants()
            .Where(element => element.Name.LocalName == "RelOp"
                && element.Descendants().Any(candidate =>
                    candidate.Name.LocalName == "Object"
                    && (string?)candidate.Attribute("Table") == "[TripReviews]"))
            .SelectMany(element => element.Descendants()
                .Where(candidate => candidate.Name.LocalName == "RunTimeCountersPerThread"))
            .Sum(element => (int?)element.Attribute("ActualLogicalReads") ?? 0);
        reads.Should().BeGreaterThan(0, "the actual plan must report TripReviews reads");
        return new PageStatistics(count, reads, planXml!);
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ApplyMigrationAsync(SqlServerTestDatabase database) =>
        await database.ExecuteScriptAsync(Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            MigrationFileName));

    private static async Task AssertIndexShapeAsync(SqlServerTestDatabase database)
    {
        var matchingColumns = await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*)
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'social.TripReviews')
              AND i.name = N'{RatingIndexName}'
              AND i.is_unique = 0
              AND i.is_disabled = 0
              AND i.has_filter = 1
              AND REPLACE(REPLACE(i.filter_definition, N'[', N''), N']', N'') = N'(tour_id IS NOT NULL)'
              AND (
                    (ic.key_ordinal = 1 AND ic.is_descending_key = 0 AND c.name = N'tour_id')
                 OR (ic.key_ordinal = 2 AND ic.is_descending_key = 0 AND c.name = N'publication_status')
                 OR (ic.key_ordinal = 0 AND ic.is_included_column = 1
                     AND c.name IN (N'overall_rating', N'booking_id'))
              );
            """);

        matchingColumns.Should().Be(4);
    }

    private const string PageQuery = """
        WITH rating AS (
            SELECT tour_id, AVG(CAST(overall_rating AS DECIMAL(10,4))) AS average_rating
            FROM social.TripReviews
            WHERE tour_id IS NOT NULL AND publication_status = 'Published'
            GROUP BY tour_id)
        SELECT tour.tour_id, rating.average_rating
        FROM commerce.Tours AS tour
        LEFT JOIN rating ON rating.tour_id = tour.tour_id
        ORDER BY CASE WHEN rating.average_rating IS NULL THEN 1 ELSE 0 END,
                 rating.average_rating DESC, tour.title ASC, tour.tour_id ASC
        OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY;
        """;

    private sealed record RatingAggregate(decimal? Average, long Count);
    private sealed record PageStatistics(
        int ResultCount,
        int TripReviewLogicalReads,
        string PlanXml);
}