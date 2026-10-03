using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Reviews;

// Task 2a only: parent contract and unchanged legacy inventory. No invented visit/media schema.
public sealed class TripReviewMigrationTests
{
    private const string MigrationName = "20260928_add_trip_review_parent.sql";
    private const string InsertParent = """
        INSERT social.TripReviews(booking_id, traveler_user_id, itinerary_id,
            overall_rating, title, content, route_pacing, csp_rating,
            publish_display_name, public_display_name, publication_status,
            policy_version, created_at, edit_deadline, updated_at)
        VALUES(1, 1, 1, 5, N'Title', N'Content', NULL, NULL,
            0, N'T.', 'Published', N'test-policy',
            '2026-09-01', '2026-09-08', '2026-09-01');
        """;

    [SqlServerFact]
    public async Task FreshSchema_HasParentAndUnchangedLegacyShape()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await AssertShapeAsync(database);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'social.Reviews')"))
            .Should().Be(10, "Task 2b linkage/evidence is deferred, not silently implemented");
    }

    [SqlServerFact]
    public async Task Upgrade_MatchesFreshFullAffectedInventory_AndPreservesLegacyData()
    {
        await using var fresh = await SqlServerTestDatabase.CreateAsync();
        await using var upgrade = await BaselineAsync();
        var before = await LegacyStateAsync(upgrade);
        await MigrateAsync(upgrade);
        await AssertShapeAsync(upgrade);
        (await TripReviewSchemaInventory.ReadAsync(upgrade)).Should().Equal(await TripReviewSchemaInventory.ReadAsync(fresh));
        (await LegacyStateAsync(upgrade)).Should().Be(before);
    }

    [SqlServerFact]
    public async Task Rerun_PreservesCanonicalInventoryAndExistingParent()
    {
        await using var database = await BaselineAsync();
        await MigrateAsync(database);
        await database.ExecuteNonQueryAsync(InsertParent);
        var inventory = await TripReviewSchemaInventory.ReadAsync(database);
        var version = await database.ExecuteScalarAsync<string>("SELECT CONVERT(varchar(18), version, 1) FROM social.TripReviews");
        await MigrateAsync(database);
        (await TripReviewSchemaInventory.ReadAsync(database)).Should().Equal(inventory);
        (await database.ExecuteScalarAsync<string>("SELECT CONVERT(varchar(18), version, 1) FROM social.TripReviews")).Should().Be(version);
    }

    [SqlServerFact]
    public async Task WrongShape_RollsBackWithoutRepairOrLegacyChanges()
    {
        await using var database = await BaselineAsync();
        await database.ExecuteNonQueryAsync("CREATE TABLE social.TripReviews(trip_review_id INT PRIMARY KEY); INSERT social.TripReviews VALUES(123);");
        var before = await LegacyStateAsync(database);
        Func<Task> migrate = () => MigrateAsync(database);
        await migrate.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews')")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT trip_review_id FROM social.TripReviews")).Should().Be(123);
        (await LegacyStateAsync(database)).Should().Be(before);
    }

    [SqlServerFact]
    public async Task DuplicateBooking_IsRejected_WithoutRejectingDistinctBookings()
    {
        await using var database = await BaselineAsync();
        await MigrateAsync(database);
        await database.ExecuteNonQueryAsync(InsertParent);
        Func<Task> duplicate = () => database.ExecuteNonQueryAsync(InsertParent);
        await duplicate.Should().ThrowAsync<SqlException>();
        await database.ExecuteNonQueryAsync(InsertParent.Replace("VALUES(1, 1, 1,", "VALUES(2, 1, 1,"));
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviews")).Should().Be(2);
    }

    [SqlServerTheory]
    [InlineData("overall_rating = 0")]
    [InlineData("overall_rating = 6")]
    [InlineData("csp_rating = 0")]
    [InlineData("csp_rating = 6")]
    [InlineData("route_pacing = 'invalid'")]
    [InlineData("edit_deadline = '2026-09-09'")]
    [InlineData("title = N''")]
    [InlineData("content = N''")]
    [InlineData("publication_status = 'Invalid'")]
    [InlineData("itinerary_id = NULL")]
    [InlineData("traveler_user_id = 999999")]
    [InlineData("itinerary_id = 999999")]
    public async Task InvalidParentInvariant_IsRejected(string mutation)
    {
        await using var database = await BaselineAsync();
        await MigrateAsync(database);
        await database.ExecuteNonQueryAsync(InsertParent);
        Func<Task> invalid = () => database.ExecuteNonQueryAsync($"UPDATE social.TripReviews SET {mutation};");
        await invalid.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    public async Task IndependentC4AndRowversion_RoundTripAndAdvanceOnUpdate()
    {
        await using var database = await BaselineAsync();
        await MigrateAsync(database);
        await database.ExecuteNonQueryAsync(InsertParent);
        var before = await database.ExecuteScalarAsync<string>("SELECT CONVERT(varchar(18), version, 1) FROM social.TripReviews");
        await database.ExecuteNonQueryAsync("UPDATE social.TripReviews SET route_pacing='tooTight', csp_rating=2;");
        (await database.ExecuteScalarAsync<string>("SELECT CONCAT(overall_rating,'|',route_pacing,'|',csp_rating) FROM social.TripReviews")).Should().Be("5|tooTight|2");
        (await database.ExecuteScalarAsync<string>("SELECT CONVERT(varchar(18), version, 1) FROM social.TripReviews")).Should().NotBe(before);
        await database.ExecuteNonQueryAsync("UPDATE social.TripReviews SET route_pacing=NULL, csp_rating=NULL;");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviews WHERE route_pacing IS NULL AND csp_rating IS NULL")).Should().Be(1);
    }

    [SqlServerFact]
    public async Task ReferencedBooking_CannotBeDeletedByCascade()
    {
        await using var database = await BaselineAsync();
        await MigrateAsync(database);
        await database.ExecuteNonQueryAsync(InsertParent.Replace("VALUES(1, 1, 1,", "VALUES(3, 1, 1,"));
        Func<Task> delete = () => database.ExecuteNonQueryAsync("DELETE commerce.Bookings WHERE booking_id=3;");
        await delete.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviews")).Should().Be(1);
    }

    [SqlServerFact]
    public async Task Rerun_WithWrongConstraint_FailsWithoutRepairingIt()
    {
        await using var database = await BaselineAsync();
        await MigrateAsync(database);
        await database.ExecuteNonQueryAsync("ALTER TABLE social.TripReviews DROP CONSTRAINT CK_TripReviews_Overall; ALTER TABLE social.TripReviews ADD CONSTRAINT CK_TripReviews_Overall CHECK(overall_rating BETWEEN 0 AND 5);");
        var before = await TripReviewSchemaInventory.ReadAsync(database);
        Func<Task> migrate = () => MigrateAsync(database);
        await migrate.Should().ThrowAsync<SqlException>();
        (await TripReviewSchemaInventory.ReadAsync(database)).Should().Equal(before);
    }

    [SqlServerFact]
    public async Task Rerun_WithIgnoreDuplicateKey_FailsWithoutChangingDataOrInventory()
    {
        await using var database = await BaselineAsync();
        await MigrateAsync(database);
        await database.ExecuteNonQueryAsync(InsertParent);
        await database.ExecuteNonQueryAsync("DROP INDEX UX_TripReviews_Booking ON social.TripReviews; CREATE UNIQUE INDEX UX_TripReviews_Booking ON social.TripReviews(booking_id) WITH(IGNORE_DUP_KEY=ON);");
        var before = await TripReviewSchemaInventory.ReadAsync(database);
        var version = await database.ExecuteScalarAsync<string>("SELECT CONVERT(varchar(18), version, 1) FROM social.TripReviews");
        Func<Task> migrate = () => MigrateAsync(database);
        await migrate.Should().ThrowAsync<SqlException>();
        (await TripReviewSchemaInventory.ReadAsync(database)).Should().Equal(before);
        (await database.ExecuteScalarAsync<string>("SELECT CONVERT(varchar(18), version, 1) FROM social.TripReviews")).Should().Be(version);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviews")).Should().Be(1);
    }

    [SqlServerFact]
    public async Task TourSubject_HasRealForeignKeyAndCannotAlsoHaveItinerarySubject()
    {
        await using var database = await BaselineAsync();
        await MigrateAsync(database);
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role,email,full_name) VALUES('TourOperator',N'operator@test.invalid',N'Operator');
            INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no) VALUES(2,N'Test',N'TestTax',N'TestLicense');
            INSERT commerce.Tours(operator_user_id,title,base_price) VALUES(2,N'Tour',0);
            """);
        await database.ExecuteNonQueryAsync(InsertParent.Replace("itinerary_id,", "tour_id,"));
        Func<Task> mixed = () => database.ExecuteNonQueryAsync("UPDATE social.TripReviews SET itinerary_id=1;");
        await mixed.Should().ThrowAsync<SqlException>();
        Func<Task> foreign = () => database.ExecuteNonQueryAsync("UPDATE social.TripReviews SET tour_id=999999;");
        await foreign.Should().ThrowAsync<SqlException>();
    }

    private static async Task<SqlServerTestDatabase> BaselineAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        try
        {
            await database.ExecuteScriptAsync(Path.Combine(AppContext.BaseDirectory, "Database", "Fixtures", "tm79_pre_migration_schema.sql"));
            await database.ExecuteNonQueryAsync("""
                INSERT dbo.Users(role,email,full_name) VALUES('Traveler',N'tm79@test.invalid',N'Test Traveler');
                INSERT planning.Itineraries(traveler_user_id,source_type,title) VALUES(1,'Manual',N'Test');
                INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
                VALUES('TM79-1',1,1,0,0,'Completed'),('TM79-2',1,1,0,0,'Completed'),('TM79-3',1,1,0,0,'Completed');
                INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating,comment,created_at)
                VALUES(1,'Tour',10,1,5,N'Legacy Tour','2026-09-01'),
                      (1,'POI',20,1,4,N'Legacy POI','2026-09-01'),
                      (1,'Tour',10,2,3,N'Different booking','2026-09-02'),
                      (1,'Operator',30,NULL,2,NULL,'2026-09-03');
                """);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static Task MigrateAsync(SqlServerTestDatabase database)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", MigrationName);
        File.Exists(path).Should().BeTrue("the TM-79 parent migration must exist before this contract can pass");
        return database.ExecuteScriptAsync(path);
    }

    private static Task<string> LegacyStateAsync(SqlServerTestDatabase database) => database.ExecuteScalarAsync<string>("""
        SELECT CONCAT((SELECT * FROM social.Reviews ORDER BY review_id FOR JSON PATH, INCLUDE_NULL_VALUES), '|',
          IDENT_CURRENT('social.Reviews'));
        """);

    private static Task AssertShapeAsync(SqlServerTestDatabase database) => database.ExecuteNonQueryAsync("""
        IF OBJECT_ID(N'social.TripReviews',N'U') IS NULL THROW 51000, 'TM-79 parent table is missing.',1;
        DECLARE @expected TABLE(name sysname, type_name sysname, max_length smallint, nullable bit, identity_column bit);
        INSERT @expected VALUES
          ('trip_review_id','bigint',8,0,1),('booking_id','bigint',8,0,0),
          ('traveler_user_id','bigint',8,0,0),('tour_id','bigint',8,1,0),('itinerary_id','bigint',8,1,0),
          ('overall_rating','tinyint',1,0,0),('title','nvarchar',400,0,0),('content','nvarchar',2000,0,0),
          ('route_pacing','varchar',9,1,0),('csp_rating','tinyint',1,1,0),('publish_display_name','bit',1,0,0),
          ('public_display_name','nvarchar',-1,0,0),('publication_status','varchar',9,0,0),
          ('policy_version','nvarchar',-1,0,0),('created_at','datetime2',8,0,0),
          ('edit_deadline','datetime2',8,0,0),('updated_at','datetime2',8,0,0),('version','timestamp',8,0,0);
        IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews')) <> 18
          OR EXISTS(SELECT * FROM @expected EXCEPT
            SELECT c.name,t.name,c.max_length,c.is_nullable,c.is_identity FROM sys.columns c JOIN sys.types t ON c.user_type_id=t.user_type_id
            WHERE c.object_id=OBJECT_ID(N'social.TripReviews'))
            THROW 51000, 'TM-79 parent column inventory is invalid.',1;
        IF NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name='version' AND system_type_id=189 AND max_length=8)
            THROW 51000, 'TM-79 rowversion is missing.',1;
        IF NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name='title' AND max_length=400 AND is_nullable=0)
            THROW 51000, 'TM-79 title shape is invalid.',1;
        IF NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name='content' AND max_length=2000 AND is_nullable=0)
            THROW 51000, 'TM-79 content shape is invalid.',1;
        IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name IN ('title','content','public_display_name','policy_version') AND collation_name <> 'Vietnamese_100_CI_AS')
            THROW 51000, 'TM-79 Unicode collation is invalid.',1;
        IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name IN ('created_at','edit_deadline','updated_at') AND scale <> 7)
            THROW 51000, 'TM-79 UTC precision is invalid.',1;
        IF NOT EXISTS(SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            WHERE i.object_id=OBJECT_ID(N'social.TripReviews') AND i.is_primary_key=1 AND i.is_disabled=0 AND ic.key_ordinal=1 AND COL_NAME(ic.object_id,ic.column_id)='trip_review_id'
              AND (SELECT COUNT(*) FROM sys.index_columns k WHERE k.object_id=i.object_id AND k.index_id=i.index_id AND k.key_ordinal>0)=1)
            THROW 51000, 'TM-79 parent PK is missing.',1;
        IF NOT EXISTS(SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            WHERE i.object_id=OBJECT_ID(N'social.TripReviews') AND i.is_unique=1 AND i.has_filter=0 AND i.is_disabled=0 AND ic.key_ordinal=1 AND COL_NAME(ic.object_id,ic.column_id)='booking_id'
              AND (SELECT COUNT(*) FROM sys.index_columns k WHERE k.object_id=i.object_id AND k.index_id=i.index_id AND k.key_ordinal>0)=1)
            THROW 51000, 'TM-79 booking uniqueness is missing.',1;
        IF (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'social.TripReviews') AND delete_referential_action=0 AND is_disabled=0 AND is_not_trusted=0) <> 4
            THROW 51000, 'TM-79 restrictive foreign keys are missing.',1;
        IF (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'social.TripReviews') AND is_disabled=0 AND is_not_trusted=0) <> 10
            THROW 51000, 'TM-79 trusted checks are missing.',1;
        """);
}