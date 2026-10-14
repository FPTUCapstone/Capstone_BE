using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class TripReviewTypedParentMigrationTests
{
    private const string Migration = "20261007_extend_trip_review_typed_parents.sql";

    [SqlServerFact]
    public async Task FreshAndUpgradeSchemasMatch_AndExistingRowsRemainUnscreened()
    {
        await using var fresh = await SqlServerTestDatabase.CreateAsync();
        await using var upgrade = await CreateLegacyDatabaseAsync();

        var reviewBefore = await upgrade.ExecuteScalarAsync<string>("""
            SELECT CONCAT(booking_id, '|', traveler_user_id, '|', itinerary_id, '|',
                title, '|', content, '|', COALESCE(policy_version, '<NULL>'))
            FROM social.TripReviews;
            """);
        var operationBefore = await upgrade.ExecuteScalarAsync<string>("""
            SELECT CONCAT(CONVERT(varchar(36), operation_id), '|', booking_id, '|',
                traveler_user_id, '|', state)
            FROM social.TripReviewMediaOperations;
            """);

        await MigrateAsync(upgrade);

        await AssertShapeAsync(fresh);
        await AssertShapeAsync(upgrade);
        (await TripReviewSchemaInventory.ReadAsync(upgrade)).Should()
            .Equal(await TripReviewSchemaInventory.ReadAsync(fresh));
        (await TripReviewSchemaInventory.ReadAsync(upgrade, mediaOnly: true)).Should()
            .Equal(await TripReviewSchemaInventory.ReadAsync(fresh, mediaOnly: true));
        (await upgrade.ExecuteScalarAsync<string>("""
            SELECT CONCAT(booking_id, '|', traveler_user_id, '|', itinerary_id, '|',
                title, '|', content, '|', COALESCE(policy_version, '<NULL>'))
            FROM social.TripReviews;
            """)).Should().Be(reviewBefore);
        (await upgrade.ExecuteScalarAsync<string>("""
            SELECT CONCAT(CONVERT(varchar(36), operation_id), '|', booking_id, '|',
                traveler_user_id, '|', state)
            FROM social.TripReviewMediaOperations;
            """)).Should().Be(operationBefore);
        (await upgrade.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM social.TripReviews WHERE policy_version IS NULL"))
            .Should().Be(1, "the migration must not claim historical moderation");
    }

    [SqlServerFact]
    public async Task FilteredUniqueness_IsNamespaceSafe()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await database.ExecuteNonQueryAsync(CommerceReview(1));
        await database.ExecuteNonQueryAsync(ServiceReview(1, 1));

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM social.TripReviews
            WHERE booking_id = 1 OR service_booking_id = 1;
            """)).Should().Be(2, "equal numeric IDs in different namespaces must coexist");

        Func<Task> duplicateCommerce = () => database.ExecuteNonQueryAsync(CommerceReview(1));
        Func<Task> duplicateService = () => database.ExecuteNonQueryAsync(ServiceReview(1, 1));
        await duplicateCommerce.Should().ThrowAsync<SqlException>();
        await duplicateService.Should().ThrowAsync<SqlException>();
    }

    [SqlServerTheory]
    [InlineData("NULL", "NULL", "NULL", "1", "NULL")]
    [InlineData("1", "1", "NULL", "1", "NULL")]
    [InlineData("1", "NULL", "NULL", "NULL", "1")]
    [InlineData("NULL", "1", "1", "NULL", "NULL")]
    [InlineData("NULL", "1", "NULL", "1", "1")]
    [InlineData("NULL", "1", "NULL", "NULL", "NULL")]
    public async Task ParentAndSubjectMisalignment_IsRejected(
        string booking, string serviceBooking, string tour, string itinerary, string poi)
    {
        await using var database = await CreateMigratedDatabaseAsync();
        Func<Task> invalid = () => database.ExecuteNonQueryAsync(Review(
            booking, serviceBooking, tour, itinerary, poi, "misaligned"));
        await invalid.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    public async Task ServiceSubjectRequiresRealPoi_AndCommerceSubjectsRemainSupported()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await database.ExecuteNonQueryAsync(CommerceReview(1));
        await database.ExecuteNonQueryAsync(CommerceTourReview(3));
        await database.ExecuteNonQueryAsync(ServiceReview(1, 1));

        Func<Task> unknownPoi = () => database.ExecuteNonQueryAsync(ServiceReview(2, 999999));
        await unknownPoi.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviews"))
            .Should().Be(4, "one preserved row and three valid canonical rows are expected");
    }

    [SqlServerFact]
    public async Task MediaOperationIdentity_IsTypedAndNamespaceSafe()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await database.ExecuteNonQueryAsync(MediaOperation(
            Guid.NewGuid(), "1", "NULL", "commerce-op"));
        await database.ExecuteNonQueryAsync(MediaOperation(
            Guid.NewGuid(), "NULL", "1", "service-op"));

        Func<Task> neither = () => database.ExecuteNonQueryAsync(
            MediaOperation(Guid.NewGuid(), "NULL", "NULL", "neither-op"));
        Func<Task> both = () => database.ExecuteNonQueryAsync(
            MediaOperation(Guid.NewGuid(), "1", "1", "both-op"));
        await neither.Should().ThrowAsync<SqlException>();
        await both.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    public async Task Rerun_IsIdempotentAndPreservesRowsAndRowversions()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await database.ExecuteNonQueryAsync(ServiceReview(1, 1));
        var inventory = await TripReviewSchemaInventory.ReadAsync(database);
        var mediaInventory = await TripReviewSchemaInventory.ReadAsync(database, mediaOnly: true);
        var state = await database.ExecuteScalarAsync<string>("""
            SELECT CONCAT((SELECT * FROM social.TripReviews ORDER BY trip_review_id FOR JSON PATH, INCLUDE_NULL_VALUES), '|',
                (SELECT * FROM social.TripReviewMediaOperations ORDER BY operation_id FOR JSON PATH, INCLUDE_NULL_VALUES));
            """);
        var version = await database.ExecuteScalarAsync<string>("""
            SELECT CONVERT(varchar(18), version, 1)
            FROM social.TripReviews WHERE service_booking_id = 1;
            """);

        await MigrateAsync(database);

        (await TripReviewSchemaInventory.ReadAsync(database)).Should().Equal(inventory);
        (await TripReviewSchemaInventory.ReadAsync(database, mediaOnly: true)).Should().Equal(mediaInventory);
        (await database.ExecuteScalarAsync<string>("""
            SELECT CONCAT((SELECT * FROM social.TripReviews ORDER BY trip_review_id FOR JSON PATH, INCLUDE_NULL_VALUES), '|',
                (SELECT * FROM social.TripReviewMediaOperations ORDER BY operation_id FOR JSON PATH, INCLUDE_NULL_VALUES));
            """)).Should().Be(state);
        (await database.ExecuteScalarAsync<string>("""
            SELECT CONVERT(varchar(18), version, 1)
            FROM social.TripReviews WHERE service_booking_id = 1;
            """)).Should().Be(version);
    }

    [SqlServerFact]
    public async Task Rerun_RejectsSameNamedFilteredIndexWithWrongKeyColumn()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            DROP INDEX UX_TripReviews_CommerceBooking ON social.TripReviews;
            CREATE UNIQUE INDEX UX_TripReviews_CommerceBooking
                ON social.TripReviews(traveler_user_id)
                WHERE booking_id IS NOT NULL;
            """);

        Func<Task> migrate = () => MigrateAsync(database);

        await migrate.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<string>("""
            SELECT c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic
              ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1
            JOIN sys.columns c
              ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE i.object_id=OBJECT_ID(N'social.TripReviews')
              AND i.name=N'UX_TripReviews_CommerceBooking';
            """)).Should().Be("traveler_user_id", "a rerun validates but never silently repairs an unknown successor");
    }

    [SqlServerTheory]
    [InlineData("social.TripReviews", "CK_TripReviews_Parent")]
    [InlineData("social.TripReviews", "CK_TripReviews_Subject")]
    [InlineData("social.TripReviewMediaOperations", "CK_TripReviewMediaOperations_Parent")]
    public async Task Rerun_RejectsSameNamedCheckWithWrongDefinition(
        string table,
        string constraint)
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await database.ExecuteNonQueryAsync($"""
            ALTER TABLE {table} DROP CONSTRAINT {constraint};
            ALTER TABLE {table} WITH CHECK ADD CONSTRAINT {constraint} CHECK (1=1);
            """);

        Func<Task> migrate = () => MigrateAsync(database);

        await migrate.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    public async Task Rerun_RejectsMissingCommerceMediaOperationForeignKey()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE social.TripReviewMediaOperations
                DROP CONSTRAINT FK_TripReviewMediaOperations_Booking;
            """);

        Func<Task> migrate = () => MigrateAsync(database);

        await migrate.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.foreign_keys
            WHERE parent_object_id=OBJECT_ID(N'social.TripReviewMediaOperations')
              AND name=N'FK_TripReviewMediaOperations_Booking';
            """)).Should().Be(0, "a rerun validates but never silently repairs an unknown successor");
    }

    [SqlServerFact]
    public async Task Upgrade_RejectsLegacyIndexWithWrongKeyColumn()
    {
        await using var database = await CreateLegacyDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            DROP INDEX UX_TripReviews_Booking ON social.TripReviews;
            CREATE UNIQUE INDEX UX_TripReviews_Booking
                ON social.TripReviews(traveler_user_id);
            """);

        Func<Task> migrate = () => MigrateAsync(database);

        await migrate.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id=OBJECT_ID(N'social.TripReviews')
              AND name IN (N'service_booking_id', N'poi_id');
            """)).Should().Be(0, "an unknown predecessor must remain untouched");
    }

    [SqlServerFact]
    public async Task Upgrade_RejectsLegacySubjectCheckWithWrongDefinition()
    {
        await using var database = await CreateLegacyDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE social.TripReviews DROP CONSTRAINT CK_TripReviews_Subject;
            ALTER TABLE social.TripReviews WITH CHECK
                ADD CONSTRAINT CK_TripReviews_Subject CHECK (1=1);
            """);

        Func<Task> migrate = () => MigrateAsync(database);

        await migrate.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id=OBJECT_ID(N'social.TripReviews')
              AND name IN (N'service_booking_id', N'poi_id');
            """)).Should().Be(0, "an unknown predecessor must remain untouched");
    }

    [SqlServerFact]
    public async Task WrongLegacyShape_FailsTransactionWithoutPartialUpgrade()
    {
        await using var database = await CreateLegacyDatabaseAsync();
        await database.ExecuteNonQueryAsync(
            "ALTER TABLE social.TripReviews ADD unexpected_column int NULL;");
        var before = await TripReviewSchemaInventory.ReadAsync(database);
        var state = await database.ExecuteScalarAsync<string>(
            "SELECT * FROM social.TripReviews FOR JSON PATH, INCLUDE_NULL_VALUES;");

        Func<Task> migrate = () => MigrateAsync(database);
        await migrate.Should().ThrowAsync<SqlException>();

        (await TripReviewSchemaInventory.ReadAsync(database)).Should().Equal(before);
        (await database.ExecuteScalarAsync<string>(
            "SELECT * FROM social.TripReviews FOR JSON PATH, INCLUDE_NULL_VALUES;"))
            .Should().Be(state);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'social.TripReviews')
              AND name IN (N'service_booking_id', N'poi_id');
            """)).Should().Be(0);
    }

    private static async Task<SqlServerTestDatabase> CreateLegacyDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        try
        {
            await database.ExecuteScriptAsync(Path.Combine(
                AppContext.BaseDirectory, "Database", "Fixtures", "tm79_pre_media_schema.sql"));
            await database.ExecuteScriptAsync(Path.Combine(
                AppContext.BaseDirectory, "Database", "migrations", "20260929_add_trip_review_media.sql"));
            await database.ExecuteScriptAsync(Path.Combine(
                AppContext.BaseDirectory, "Database", "migrations", "20260930_add_trip_review_media_recovery.sql"));
            await SeedServiceDependenciesAsync(database);
            await database.ExecuteNonQueryAsync(LegacyMediaOperation(
                Guid.Parse("00000000-0000-0000-0000-000000000079"), "2", "NULL", "preserved-op"));
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<SqlServerTestDatabase> CreateMigratedDatabaseAsync()
    {
        var database = await CreateLegacyDatabaseAsync();
        try
        {
            await MigrateAsync(database);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static Task SeedServiceDependenciesAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            INSERT catalog.POICategories(name) VALUES(N'TM79');
            INSERT catalog.POIs(category_id,name,latitude,longitude) VALUES(1,N'Canonical POI',10,106);
            INSERT dbo.Users(role,email,full_name) VALUES('TourOperator',N'tm79-operator@test.invalid',N'Operator');
            INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no)
            VALUES(3,N'TM79 Operator',N'TM79-TAX',N'TM79-LICENSE');
            INSERT commerce.Tours(operator_user_id,title,base_price) VALUES(3,N'TM79 Tour',0);
            INSERT commercial.ServiceProviders(name,service_category) VALUES(N'TM79 Provider','Hotel');
            INSERT commercial.Services(provider_id,service_category,name,poi_id,price_amount,price_unit)
            VALUES(1,'Hotel',N'Canonical Service',1,0,'PerNight'),
                  (1,'Hotel',N'Unsupported Service',NULL,0,'PerNight');
            INSERT commercial.ServiceBookings(traveler_user_id,service_id,start_datetime,total_price,status,provider_reference)
            VALUES(1,1,'2026-09-01',0,'Completed',N'SVC-1'),
                  (1,2,'2026-09-01',0,'Completed',N'SVC-2');
            """);

    private static Task MigrateAsync(SqlServerTestDatabase database)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", Migration);
        File.Exists(path).Should().BeTrue("the R3 typed-parent migration must be packaged");
        return database.ExecuteScriptAsync(path);
    }

    private static string CommerceReview(long bookingId) =>
        Review(bookingId.ToString(), "NULL", "NULL", "1", "NULL", $"commerce-{bookingId}");

    private static string CommerceTourReview(long bookingId) =>
        Review(bookingId.ToString(), "NULL", "1", "NULL", "NULL", $"tour-{bookingId}");

    private static string ServiceReview(long serviceBookingId, long poiId) =>
        Review("NULL", serviceBookingId.ToString(), "NULL", "NULL", poiId.ToString(),
            $"service-{serviceBookingId}");

    private static string Review(string booking, string serviceBooking, string tour,
        string itinerary, string poi, string title) => $"""
        INSERT social.TripReviews(
            booking_id, service_booking_id, traveler_user_id, tour_id, itinerary_id, poi_id,
            overall_rating, title, content, publish_display_name, public_display_name,
            publication_status, policy_version, created_at, edit_deadline, updated_at)
        VALUES({booking}, {serviceBooking}, 1, {tour}, {itinerary}, {poi},
            5, N'{title}', N'Content', 0, N'T.', 'Published', NULL,
            '2026-09-01', '2026-09-08', '2026-09-01');
        """;

    private static string MediaOperation(Guid id, string booking, string serviceBooking,
        string publicId) => $"""
        INSERT social.TripReviewMediaOperations(
            operation_id,batch_id,booking_id,service_booking_id,traveler_user_id,sort_order,
            public_id,state,content_type,extension,input_byte_length,width,height,created_at,updated_at)
        VALUES('{id}','{Guid.NewGuid()}',{booking},{serviceBooking},1,0,
            '{publicId}','Reserved','image/png','.png',1,1,1,'2026-09-01','2026-09-01');
        """;

    private static string LegacyMediaOperation(Guid id, string booking, string ignoredServiceBooking,
        string publicId) => $"""
        INSERT social.TripReviewMediaOperations(
            operation_id,batch_id,booking_id,traveler_user_id,sort_order,
            public_id,state,content_type,extension,input_byte_length,width,height,created_at,updated_at)
        VALUES('{id}','{Guid.NewGuid()}',{booking},1,0,
            '{publicId}','Reserved','image/png','.png',1,1,1,'2026-09-01','2026-09-01');
        """;

    private static Task AssertShapeAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'booking_id' AND is_nullable=1)
                THROW 51000, 'booking_id must be nullable.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'service_booking_id' AND is_nullable=1)
                THROW 51000, 'service_booking_id is missing.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'poi_id' AND is_nullable=1)
                THROW 51000, 'poi_id is missing.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'UX_TripReviews_CommerceBooking' AND is_unique=1 AND has_filter=1 AND filter_definition=N'([booking_id] IS NOT NULL)')
                THROW 51000, 'commerce filtered uniqueness is missing.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'UX_TripReviews_ServiceBooking' AND is_unique=1 AND has_filter=1 AND filter_definition=N'([service_booking_id] IS NOT NULL)')
                THROW 51000, 'service filtered uniqueness is missing.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'social.TripReviews') AND name=N'FK_TripReviews_ServiceBooking' AND is_disabled=0 AND is_not_trusted=0)
                THROW 51000, 'service booking FK is missing.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'social.TripReviews') AND name=N'FK_TripReviews_Poi' AND is_disabled=0 AND is_not_trusted=0)
                THROW 51000, 'POI FK is missing.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'social.TripReviews') AND name=N'CK_TripReviews_Parent' AND is_disabled=0 AND is_not_trusted=0)
                THROW 51000, 'parent check is missing.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviewMediaOperations') AND name=N'booking_id' AND is_nullable=1)
                THROW 51000, 'media commerce identity must be nullable.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviewMediaOperations') AND name=N'service_booking_id' AND is_nullable=1)
                THROW 51000, 'media service identity is missing.', 1;
            """);
}