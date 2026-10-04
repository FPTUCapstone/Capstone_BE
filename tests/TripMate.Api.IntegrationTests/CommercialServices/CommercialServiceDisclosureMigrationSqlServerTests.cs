using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.CommercialServices;

public sealed class CommercialServiceDisclosureMigrationSqlServerTests
{
    private const string MigrationFileName =
        "20261003_add_commercial_service_disclosures.sql";
    private const string DevelopmentSeedFileName =
        "20261003_uc30_commercial_services_development.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LegacySchema_MigrationBackfillsCanonicalDisclosureColumnsAndIsRepeatable()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await RevertToPreUc30SchemaAsync(database);

        await database.ExecuteNonQueryAsync("""
            INSERT commercial.ServiceProviders(name, service_category, status)
            VALUES (N'Legacy hotel provider', 'Hotel', 'Active');

            DECLARE @providerId BIGINT = SCOPE_IDENTITY();
            INSERT commercial.Services(
                provider_id,
                service_category,
                name,
                price_amount,
                price_unit)
            VALUES (
                @providerId,
                'Hotel',
                N'Legacy hotel room',
                900000.00,
                'PerNight');
            """);

        await database.ExecuteScriptAsync(GetMigrationPath());
        await database.ExecuteScriptAsync(GetMigrationPath());

        var canonicalRowCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM commercial.Services
            WHERE currency_code = 'VND'
              AND price_includes_tax = 0
              AND last_updated_at IS NOT NULL;
            """);
        canonicalRowCount.Should().Be(1);

        var canonicalConstraintCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'commercial.Services')
              AND name IN (
                  N'CK_Services_CurrencyCode',
                  N'CK_Services_RefundableDepositNonNegative',
                  N'CK_Services_CoverImageHttps',
                  N'CK_Services_PickupInstructionsLength',
                  N'CK_Services_CancellationSummaryLength');
            """);
        canonicalConstraintCount.Should().Be(5);

        var canonicalColumnCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns AS column_info
            INNER JOIN sys.types AS type_info
                ON type_info.user_type_id = column_info.user_type_id
            WHERE column_info.object_id = OBJECT_ID(N'commercial.Services')
              AND (
                    (column_info.name = N'currency_code'
                        AND type_info.name = N'varchar'
                        AND column_info.max_length = 3
                        AND column_info.is_nullable = 0)
                 OR (column_info.name = N'price_includes_tax'
                        AND type_info.name = N'bit'
                        AND column_info.is_nullable = 0)
                 OR (column_info.name = N'refundable_deposit_amount'
                        AND type_info.name = N'decimal'
                        AND column_info.precision = 12
                        AND column_info.scale = 2
                        AND column_info.is_nullable = 1)
                 OR (column_info.name = N'cover_image_url'
                        AND type_info.name = N'nvarchar'
                        AND column_info.max_length = 1000
                        AND column_info.is_nullable = 1)
                 OR (column_info.name = N'fulfilment_location_label'
                        AND type_info.name = N'nvarchar'
                        AND column_info.max_length = 600
                        AND column_info.is_nullable = 1)
                 OR (column_info.name = N'pickup_or_arrival_instructions'
                        AND type_info.name = N'nvarchar'
                        AND column_info.max_length = 2000
                        AND column_info.is_nullable = 1)
                 OR (column_info.name = N'cancellation_policy_summary'
                        AND type_info.name = N'nvarchar'
                        AND column_info.max_length = 1000
                        AND column_info.is_nullable = 1)
                 OR (column_info.name = N'last_updated_at'
                        AND type_info.name = N'datetime2'
                        AND column_info.is_nullable = 0)
              );
            """);
        canonicalColumnCount.Should().Be(8);

        var caseInsensitiveSearchColumnCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id IN (
                    OBJECT_ID(N'commercial.Services'),
                    OBJECT_ID(N'commercial.ServiceProviders'))
              AND name = N'name'
              AND collation_name = N'Vietnamese_100_CI_AS';
            """);
        caseInsensitiveSearchColumnCount.Should().Be(2);

        var canonicalDefaultCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.default_constraints
            WHERE parent_object_id = OBJECT_ID(N'commercial.Services')
              AND name IN (
                  N'DF_Services_CurrencyCode',
                  N'DF_Services_PriceIncludesTax',
                  N'DF_Services_LastUpdatedAt');
            """);
        canonicalDefaultCount.Should().Be(3);

        var triggerCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.triggers
            WHERE parent_id = OBJECT_ID(N'commercial.Services')
              AND name = N'TR_Services_SetLastUpdatedAt';
            """);
        triggerCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task NamedWrongShapeConstraint_MigrationRepairsItAndTriggerKeepsLastUpdatedCurrent()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        await database.ExecuteNonQueryAsync("""
            ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_CurrencyCode;
            ALTER TABLE commercial.Services ADD CONSTRAINT CK_Services_CurrencyCode
                CHECK (currency_code IN ('VND', 'USD'));
            ALTER TABLE commercial.Services DROP CONSTRAINT DF_Services_CurrencyCode;
            ALTER TABLE commercial.Services ADD CONSTRAINT DF_Services_CurrencyCode
                DEFAULT 'USD' FOR currency_code;

            INSERT commercial.ServiceProviders(name, service_category, status)
            VALUES (N'Repair verification provider', 'Vehicle', 'Active');

            DECLARE @providerId BIGINT = SCOPE_IDENTITY();
            INSERT commercial.Services(
                provider_id,
                service_category,
                name,
                price_amount,
                price_unit,
                currency_code,
                last_updated_at)
            VALUES (
                @providerId,
                'Vehicle',
                N'Repair verification vehicle',
                200000.00,
                'PerDay',
                'VND',
                DATEADD(DAY, -1, SYSUTCDATETIME()));
            """);

        await database.ExecuteScriptAsync(GetMigrationPath());

        Func<Task> insertUnsupportedCurrency = async () => await database.ExecuteNonQueryAsync("""
            INSERT commercial.Services(
                provider_id,
                service_category,
                name,
                price_amount,
                price_unit,
                currency_code)
            SELECT TOP (1)
                provider_id,
                'Vehicle',
                N'Unsupported currency vehicle',
                100000.00,
                'PerDay',
                'USD'
            FROM commercial.ServiceProviders
            WHERE name = N'Repair verification provider';
            """);
        await insertUnsupportedCurrency.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();

        await database.ExecuteNonQueryAsync("""
            INSERT commercial.Services(
                provider_id,
                service_category,
                name,
                price_amount,
                price_unit)
            SELECT TOP (1)
                provider_id,
                'Vehicle',
                N'Canonical default vehicle',
                100000.00,
                'PerDay'
            FROM commercial.ServiceProviders
            WHERE name = N'Repair verification provider';
            """);
        var defaultWasRepaired = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM commercial.Services
            WHERE name = N'Canonical default vehicle'
              AND currency_code = 'VND';
            """);
        defaultWasRepaired.Should().Be(1);

        await database.ExecuteNonQueryAsync("""
            UPDATE commercial.Services
            SET name = N'Repair verification vehicle updated'
            WHERE name = N'Repair verification vehicle';
            """);

        var triggerUpdatedRowCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM commercial.Services
            WHERE name = N'Repair verification vehicle updated'
              AND last_updated_at > DATEADD(HOUR, -1, SYSUTCDATETIME());
            """);
        triggerUpdatedRowCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DevelopmentSeed_IsRepeatableAndDoesNotUseAProviderWithTheWrongCategory()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT commercial.ServiceProviders(name, service_category, status)
            VALUES (N'Da Nang Ride', 'Restaurant', 'Inactive');
            """);

        await database.ExecuteScriptAsync(GetDevelopmentSeedPath());
        await database.ExecuteScriptAsync(GetDevelopmentSeedPath());

        var seededServiceCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM commercial.Services
            WHERE name IN (
                N'Honda Wave 110cc',
                N'Deluxe River View Room',
                N'Vietnamese Set Menu for Two');
            """);
        seededServiceCount.Should().Be(3);

        var mismatchedSeededServiceCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM commercial.Services AS service
            INNER JOIN commercial.ServiceProviders AS provider
                ON provider.provider_id = service.provider_id
            WHERE service.name = N'Honda Wave 110cc'
              AND provider.service_category <> service.service_category;
            """);
        mismatchedSeededServiceCount.Should().Be(0);
    }

    private static async Task RevertToPreUc30SchemaAsync(SqlServerTestDatabase database)
    {
        await database.ExecuteNonQueryAsync("""
            DROP TRIGGER IF EXISTS commercial.TR_Services_SetLastUpdatedAt;
            ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_CurrencyCode;
            ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_RefundableDepositNonNegative;
            ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_CoverImageHttps;
            ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_PickupInstructionsLength;
            ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_CancellationSummaryLength;
            ALTER TABLE commercial.Services DROP CONSTRAINT DF_Services_CurrencyCode;
            ALTER TABLE commercial.Services DROP CONSTRAINT DF_Services_PriceIncludesTax;
            ALTER TABLE commercial.Services DROP CONSTRAINT DF_Services_LastUpdatedAt;

            ALTER TABLE commercial.Services DROP COLUMN currency_code;
            ALTER TABLE commercial.Services DROP COLUMN price_includes_tax;
            ALTER TABLE commercial.Services DROP COLUMN refundable_deposit_amount;
            ALTER TABLE commercial.Services DROP COLUMN cover_image_url;
            ALTER TABLE commercial.Services DROP COLUMN fulfilment_location_label;
            ALTER TABLE commercial.Services DROP COLUMN pickup_or_arrival_instructions;
            ALTER TABLE commercial.Services DROP COLUMN cancellation_policy_summary;
            ALTER TABLE commercial.Services DROP COLUMN last_updated_at;
            """);
    }

    private static string GetMigrationPath() =>
        Path.Combine(AppContext.BaseDirectory, "Database", "migrations", MigrationFileName);

    private static string GetDevelopmentSeedPath() =>
        Path.Combine(AppContext.BaseDirectory, "Database", "seeds", DevelopmentSeedFileName);
}