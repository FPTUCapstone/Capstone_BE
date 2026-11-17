using FluentAssertions;

using Microsoft.Data.SqlClient;

namespace TripMate.Api.IntegrationTests.Infrastructure;

[Collection(nameof(TripMateApiFactory))]
public sealed class ServiceProviderWorkflowMigrationSqlServerTests
{
    private static readonly string[] MigrationNames =
    [
        "20261114_tm225_account_provisioning.sql",
        "20261114_tm225_service_request_lifecycle.sql",
    ];

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LegacyUpgrade_PreservesRecords_AndMatchesFreshAffectedColumnInventory()
    {
        await using var upgraded = await SqlServerTestDatabase.CreateEmptyAsync();
        await upgraded.ExecuteScriptAsync(Path.Combine(AppContext.BaseDirectory,
            "Database", "Fixtures", "tm225_pre_migration_schema.sql"));
        await upgraded.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role, email, full_name)
                VALUES ('Traveler', N'tm225-legacy@example.com', N'Legacy Traveler');
            DECLARE @traveler BIGINT = SCOPE_IDENTITY();
            INSERT commercial.ServiceProviders(name, service_category)
                VALUES (N'Legacy hotel', 'Hotel');
            DECLARE @provider BIGINT = SCOPE_IDENTITY();
            INSERT commercial.Services(provider_id, service_category, name, price_amount, price_unit)
                VALUES (@provider, 'Hotel', N'Legacy room', 500000, 'PerNight');
            DECLARE @service BIGINT = SCOPE_IDENTITY();
            INSERT commercial.ServiceBookings(traveler_user_id, service_id, start_datetime,
                quantity, total_price, status)
                VALUES (@traveler, @service, '2026-10-01T01:00:00', 1, 500000, 'Confirmed');
            """);

        await ApplyMigrationsAsync(upgraded);
        await ApplyMigrationsAsync(upgraded);

        (await upgraded.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM commercial.ServiceBookings
            WHERE status = 'Confirmed' AND total_price = 500000
              AND request_submitted_at IS NULL;
            """)).Should().Be(1);
        (await upgraded.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.Users
            WHERE email = N'tm225-legacy@example.com' AND must_change_password = 0;
            """)).Should().Be(1);
        (await upgraded.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM commercial.ServiceProviders
            WHERE name = N'Legacy hotel' AND user_id IS NULL;
            """)).Should().Be(1);

        await using var fresh = await SqlServerTestDatabase.CreateAsync();
        (await InventoryAsync(upgraded)).Should().Be(await InventoryAsync(fresh));
        (await ConstraintInventoryAsync(upgraded)).Should().Be(await ConstraintInventoryAsync(fresh));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_MigrationsAndHistoricalRoleUpgradeAreSafeToRepeat()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var before = await InventoryAsync(database);
        var constraintsBefore = await ConstraintInventoryAsync(database);
        await ApplyMigrationsAsync(database);
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role, email, full_name)
                VALUES ('ServiceProvider', N'tm225-provider@example.com', N'Provider');
            """);
        await database.ExecuteScriptAsync(Path.Combine(AppContext.BaseDirectory,
            "Database", "migrations", "20261007_allow_staff_role.sql"));
        await ApplyMigrationsAsync(database);
        (await InventoryAsync(database)).Should().Be(before);
        (await ConstraintInventoryAsync(database)).Should().Be(constraintsBefore);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("DROP INDEX UX_ProviderPoiLinks_Poi ON commercial.ProviderPoiLinks; CREATE INDEX UX_ProviderPoiLinks_Poi ON commercial.ProviderPoiLinks(poi_id);")]
    [InlineData("DROP INDEX UX_ServiceProviderRequestOperations_ActorKey ON commercial.ServiceProviderRequestOperations; CREATE UNIQUE INDEX UX_ServiceProviderRequestOperations_ActorKey ON commercial.ServiceProviderRequestOperations(idempotency_key, actor_user_id);")]
    [InlineData("DROP INDEX IX_ServiceBookings_RequestLifecycle ON commercial.ServiceBookings; ALTER TABLE commercial.ServiceBookings ALTER COLUMN request_submitted_at DATETIMEOFFSET NULL;")]
    public async Task SameNameWrongShapeObjects_RejectUpgrade(string corruption)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(corruption);
        var apply = () => ApplyMigrationsAsync(database);
        await apply.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task MissingForeignKeys_AndWrongShapeOwnedChecks_AreCanonicalized()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var expected = await ConstraintInventoryAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE commercial.ProviderPoiLinks DROP CONSTRAINT FK_ProviderPoiLinks_Poi;
            ALTER TABLE commercial.ServiceBookings DROP CONSTRAINT FK_ServiceBookings_Itinerary;
            ALTER TABLE commercial.ServiceProviderRequestOperations DROP CONSTRAINT CK_ServiceProviderRequestOperations_Type;
            ALTER TABLE commercial.ServiceProviderRequestOperations ADD CONSTRAINT CK_ServiceProviderRequestOperations_Type
                CHECK (operation_type = 'Decision');
            """);
        await ApplyMigrationsAsync(database);
        (await ConstraintInventoryAsync(database)).Should().Be(expected);
    }

    private static async Task ApplyMigrationsAsync(SqlServerTestDatabase database)
    {
        foreach (var name in MigrationNames)
            await database.ExecuteScriptAsync(Path.Combine(AppContext.BaseDirectory, "Database", "migrations", name));
    }

    private static Task<string> InventoryAsync(SqlServerTestDatabase database) =>
        database.ExecuteScalarAsync<string>("""
            SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), CONCAT(
                SCHEMA_NAME(t.schema_id), '.', t.name, '.', c.name, ':', ty.name, ':',
                c.max_length, ':', c.precision, ':', c.scale, ':', c.is_nullable, ':',
                c.is_identity, ':', ISNULL(dc.definition, ''))), '|')
                WITHIN GROUP (ORDER BY SCHEMA_NAME(t.schema_id), t.name, c.name)
            FROM sys.tables AS t
            JOIN sys.columns AS c ON c.object_id = t.object_id
            JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints AS dc
                ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE (SCHEMA_NAME(t.schema_id) = 'commercial'
                    AND t.name IN ('ProviderPoiLinks','ServiceProviderRequestOperations'))
                OR (SCHEMA_NAME(t.schema_id) = 'dbo' AND t.name = 'Users'
                    AND c.name IN ('role','must_change_password'))
                OR (SCHEMA_NAME(t.schema_id) = 'commercial' AND t.name = 'ServiceProviders'
                    AND c.name = 'user_id')
                OR (SCHEMA_NAME(t.schema_id) = 'commercial' AND t.name = 'ServiceBookings'
                    AND c.name IN ('status','request_submitted_at','actual_amount','rejection_reason',
                        'responded_by_user_id','responded_at','outcome_by_user_id','outcome_at',
                        'is_auto_completed','row_version','itinerary_id','service_option','special_request'));
            """);

    private static Task<string> ConstraintInventoryAsync(SqlServerTestDatabase database) =>
        database.ExecuteScalarAsync<string>("""
            SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), entry), '|') WITHIN GROUP (ORDER BY entry)
            FROM (
                SELECT CONCAT('CHECK:', OBJECT_SCHEMA_NAME(c.parent_object_id), '.',
                    OBJECT_NAME(c.parent_object_id), '.', c.name, ':', c.definition, ':',
                    c.is_disabled, ':', c.is_not_trusted) AS entry
                FROM sys.check_constraints AS c
                WHERE c.name IN ('CK_Users_Role','CK_ServiceBookings_RequestStatus',
                    'CK_ServiceProviderRequestOperations_Type','CK_ServiceProviderRequestOperations_Response')
                UNION ALL
                SELECT CONCAT('FK:', OBJECT_SCHEMA_NAME(f.parent_object_id), '.',
                    OBJECT_NAME(f.parent_object_id), '.', f.name, ':', COL_NAME(fc.parent_object_id, fc.parent_column_id),
                    ':', OBJECT_SCHEMA_NAME(f.referenced_object_id), '.', OBJECT_NAME(f.referenced_object_id), '.',
                    COL_NAME(fc.referenced_object_id, fc.referenced_column_id), ':',
                    f.delete_referential_action, ':', f.update_referential_action, ':', f.is_disabled, ':', f.is_not_trusted)
                FROM sys.foreign_keys AS f
                JOIN sys.foreign_key_columns AS fc ON fc.constraint_object_id = f.object_id
                WHERE f.name IN ('FK_ServiceProviders_User','FK_ProviderPoiLinks_Provider','FK_ProviderPoiLinks_Poi',
                    'FK_ServiceBookings_RespondedBy','FK_ServiceBookings_OutcomeBy','FK_ServiceBookings_Itinerary',
                    'FK_ServiceProviderRequestOperations_Booking','FK_ServiceProviderRequestOperations_Actor')
                UNION ALL
                SELECT CONCAT('INDEX:', OBJECT_SCHEMA_NAME(i.object_id), '.', OBJECT_NAME(i.object_id), '.',
                    i.name, ':', i.is_unique, ':', i.is_disabled, ':', i.filter_definition, ':', ic.key_ordinal, ':',
                    COL_NAME(ic.object_id, ic.column_id), ':', ic.is_descending_key, ':', ic.is_included_column)
                FROM sys.indexes AS i
                JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.name IN ('PK_ProviderPoiLinks','PK_ServiceProviderRequestOperations',
                    'UX_ServiceProviders_User','UX_ProviderPoiLinks_Poi',
                    'UX_ServiceProviderRequestOperations_ActorKey','IX_ServiceBookings_RequestLifecycle')
            ) AS inventory;
            """);
}