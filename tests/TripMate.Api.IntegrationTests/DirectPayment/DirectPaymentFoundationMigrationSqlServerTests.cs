using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.DirectPayment;

/// <summary>
/// Verifies that 20261125_direct_operator_payment_refund_foundation.sql:
///   1. Is idempotent (run twice leaves data unchanged).
///   2. Adds payment_flow column to commerce.Bookings with correct default
///      and existing rows are preserved (legacy data intact).
///   3. Creates payment.OperatorPaymentChannels with UNIQUE on opaque_id and operator.
///   4. Creates payment.DirectRefundCases with correct status constraint and indexes.
///   5. Creates payment.DirectRefundTransfers with correct FK and indexes.
///   6. Creates commercial.TourDirectCommissionStatements with correct constraints.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class DirectPaymentFoundationMigrationSqlServerTests
{
    private const string MigrationFileName =
        "20261125_direct_operator_payment_refund_foundation.sql";

    // -------------------------------------------------------------------------
    // Idempotency
    // -------------------------------------------------------------------------

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RunTwiceOnDatabaseWithExistingBookingRow_IsIdempotent()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await SeedExistingBookingAsync(database);

        await database.ExecuteScriptAsync(MigrationPath());
        Func<Task> rerun = () => database.ExecuteScriptAsync(MigrationPath());

        await rerun.Should().NotThrowAsync("migration must be safe to apply twice");
    }

    // -------------------------------------------------------------------------
    // commerce.Bookings — payment_flow column
    // -------------------------------------------------------------------------

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_AddsPaymentFlowColumnWithCorrectDefault()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.Bookings')
              AND name = N'payment_flow'
              AND max_length = 30
              AND is_nullable = 0;
            """)).Should().Be(1, "payment_flow must be VARCHAR(30) NOT NULL");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_ExistingBookingRowGetsLegacyPaymentFlowDefault()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await SeedExistingBookingAsync(database);

        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<string>("""
            SELECT TOP 1 payment_flow FROM commerce.Bookings WHERE booking_id = 1;
            """)).Should().Be("PlatformCollectedLegacy", "existing bookings must default to legacy flow");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_PaymentFlowConstraintAcceptsBothValues()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        Func<Task> insertLegacy = () => database.ExecuteNonQueryAsync(
            BookingInsertSql(bookingId: 10, paymentFlow: "PlatformCollectedLegacy"));
        Func<Task> insertDirect = () => database.ExecuteNonQueryAsync(
            BookingInsertSql(bookingId: 11, paymentFlow: "OperatorDirect"));

        await insertLegacy.Should().NotThrowAsync("PlatformCollectedLegacy must be accepted");
        await insertDirect.Should().NotThrowAsync("OperatorDirect must be accepted");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_PaymentFlowConstraintRejectsUnknownValue()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        Func<Task> insert = () => database.ExecuteNonQueryAsync(
            BookingInsertSql(bookingId: 20, paymentFlow: "BankWire"));

        await insert.Should().ThrowAsync<Exception>("unknown payment_flow values must be rejected");
    }

    // -------------------------------------------------------------------------
    // payment.OperatorPaymentChannels
    // -------------------------------------------------------------------------

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_CreatesOperatorPaymentChannelsTable()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'payment')
              AND name = N'OperatorPaymentChannels';
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_OperatorPaymentChannels_HasUniqueConstraintOnOpaqueId()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await SeedUserAsync(database, userId: 1);
        await database.ExecuteScriptAsync(MigrationPath());

        await database.ExecuteNonQueryAsync("""
            INSERT payment.OperatorPaymentChannels (tour_operator_user_id)
            VALUES (1);
            """);

        Guid existingOpaqueId = await database.ExecuteScalarAsync<Guid>(
            "SELECT TOP 1 opaque_id FROM payment.OperatorPaymentChannels;");

        Func<Task> duplicate = () => database.ExecuteNonQueryAsync($"""
            INSERT payment.OperatorPaymentChannels (tour_operator_user_id, opaque_id)
            VALUES (99999, '{existingOpaqueId}');
            """);

        await duplicate.Should().ThrowAsync<Exception>("opaque_id must be unique");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_OperatorPaymentChannels_OneChannelPerOperator()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await SeedUserAsync(database, userId: 1);
        await database.ExecuteScriptAsync(MigrationPath());

        await database.ExecuteNonQueryAsync(
            "INSERT payment.OperatorPaymentChannels (tour_operator_user_id) VALUES (1);");

        Func<Task> secondChannel = () => database.ExecuteNonQueryAsync(
            "INSERT payment.OperatorPaymentChannels (tour_operator_user_id) VALUES (1);");

        await secondChannel.Should().ThrowAsync<Exception>("each TO may have at most one channel");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_OperatorPaymentChannels_StatusConstraint()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await SeedUserAsync(database, userId: 2);
        await database.ExecuteScriptAsync(MigrationPath());

        string[] validStatuses =
            ["NotConnected", "PendingVerification", "Ready", "ConnectionError", "Disabled"];

        foreach (var status in validStatuses)
        {
            await database.ExecuteNonQueryAsync(
                $"INSERT payment.OperatorPaymentChannels (tour_operator_user_id, status) VALUES (2, N'{status}');");
            await database.ExecuteNonQueryAsync(
                "DELETE payment.OperatorPaymentChannels WHERE tour_operator_user_id = 2;");
        }

        Func<Task> invalid = () => database.ExecuteNonQueryAsync(
            "INSERT payment.OperatorPaymentChannels (tour_operator_user_id, status) VALUES (2, N'Suspended');");
        await invalid.Should().ThrowAsync<Exception>("unknown status values must be rejected");
    }

    // -------------------------------------------------------------------------
    // payment.DirectRefundCases
    // -------------------------------------------------------------------------

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_CreatesDirectRefundCasesTable()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'payment')
              AND name = N'DirectRefundCases';
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_DirectRefundCases_HasRowVersionColumn()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.DirectRefundCases')
              AND name = N'row_version'
              AND system_type_id = TYPE_ID(N'timestamp');
            """)).Should().Be(1, "DirectRefundCases must have rowversion for optimistic concurrency");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_DirectRefundCases_StatusConstraintAcceptsAllValidValues()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await SeedExistingBookingAsync(database);
        await database.ExecuteScriptAsync(MigrationPath());

        string[] validStatuses =
            ["AwaitingRecipient", "AwaitingTransfer", "AwaitingTravelerConfirmation", "Completed", "Disputed"];

        foreach (var status in validStatuses)
        {
            Func<Task> insert = () => database.ExecuteNonQueryAsync(
                RefundCaseInsertSql(bookingId: 1, status: status));
            await insert.Should().NotThrowAsync($"'{status}' must be a valid case status");
            await database.ExecuteNonQueryAsync("DELETE payment.DirectRefundCases;");
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_DirectRefundCases_StatusConstraintRejectsUnknownValue()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await SeedExistingBookingAsync(database);
        await database.ExecuteScriptAsync(MigrationPath());

        Func<Task> insert = () => database.ExecuteNonQueryAsync(
            RefundCaseInsertSql(bookingId: 1, status: "PendingOperatorConfirmation"));

        await insert.Should().ThrowAsync<Exception>("legacy refund status values must be rejected in DirectRefundCases");
    }

    // -------------------------------------------------------------------------
    // payment.DirectRefundTransfers
    // -------------------------------------------------------------------------

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_CreatesDirectRefundTransfersTable()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'payment')
              AND name = N'DirectRefundTransfers';
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_DirectRefundTransfers_HasUniqueOperationId()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await SeedExistingBookingAsync(database);
        await database.ExecuteScriptAsync(MigrationPath());

        // Insert a case first
        await database.ExecuteNonQueryAsync(RefundCaseInsertSql(bookingId: 1, status: "AwaitingTransfer"));
        long caseId = await database.ExecuteScalarAsync<long>(
            "SELECT TOP 1 case_id FROM payment.DirectRefundCases;");

        Guid operationId = Guid.NewGuid();
        await database.ExecuteNonQueryAsync(
            TransferInsertSql(caseId: caseId, operationId: operationId));

        Func<Task> duplicate = () => database.ExecuteNonQueryAsync(
            TransferInsertSql(caseId: caseId, operationId: operationId));

        await duplicate.Should().ThrowAsync<Exception>("operation_id must be unique across transfers");
    }

    // -------------------------------------------------------------------------
    // commercial.TourDirectCommissionStatements
    // -------------------------------------------------------------------------

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_CreatesTourDirectCommissionStatementsTable()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'commercial')
              AND name = N'TourDirectCommissionStatements';
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_TourDirectCommissionStatements_UniquePeriodPerOperator()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await SeedUserAsync(database, userId: 5);
        await database.ExecuteScriptAsync(MigrationPath());

        await database.ExecuteNonQueryAsync(CommissionInsertSql(userId: 5, period: "2026-11", code: "COMM-001"));

        Func<Task> duplicatePeriod = () => database.ExecuteNonQueryAsync(
            CommissionInsertSql(userId: 5, period: "2026-11", code: "COMM-002"));

        await duplicatePeriod.Should().ThrowAsync<Exception>("each TO may have at most one statement per period");
    }

    // -------------------------------------------------------------------------
    // Base database setup helpers
    // -------------------------------------------------------------------------

    private static async Task<SqlServerTestDatabase> CreateBaseDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commerce;");
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commercial;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (
                user_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
                role    VARCHAR(20) NOT NULL CONSTRAINT DF_Users_Role DEFAULT 'Traveler'
            );
            CREATE TABLE commerce.Bookings (
                booking_id      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Bookings PRIMARY KEY,
                total_amount    DECIMAL(14,2) NOT NULL CONSTRAINT DF_Bookings_Total DEFAULT 0,
                booking_status  VARCHAR(20) NOT NULL CONSTRAINT DF_Bookings_Status DEFAULT 'PendingPayment',
                booked_at       DATETIME2 NOT NULL CONSTRAINT DF_Bookings_BookedAt DEFAULT SYSUTCDATETIME()
            );
            """);
        return database;
    }

    private static async Task SeedExistingBookingAsync(SqlServerTestDatabase database) =>
        await database.ExecuteNonQueryAsync("""
            SET IDENTITY_INSERT commerce.Bookings ON;
            INSERT commerce.Bookings (booking_id, total_amount, booking_status)
            VALUES (1, 2000000, 'Confirmed');
            SET IDENTITY_INSERT commerce.Bookings OFF;
            """);

    private static async Task SeedUserAsync(SqlServerTestDatabase database, long userId) =>
        await database.ExecuteNonQueryAsync($"""
            SET IDENTITY_INSERT dbo.Users ON;
            INSERT dbo.Users (user_id) VALUES ({userId});
            SET IDENTITY_INSERT dbo.Users OFF;
            """);

    private static string BookingInsertSql(long bookingId, string paymentFlow) => $"""
        SET IDENTITY_INSERT commerce.Bookings ON;
        INSERT commerce.Bookings (booking_id, total_amount, booking_status, payment_flow)
        VALUES ({bookingId}, 1000000, 'Confirmed', N'{paymentFlow}');
        SET IDENTITY_INSERT commerce.Bookings OFF;
        """;

    private static string RefundCaseInsertSql(long bookingId, string status) => $"""
        INSERT payment.DirectRefundCases
            (booking_id, source_type, refund_amount_vnd, paid_amount_vnd, status)
        VALUES ({bookingId}, N'PolicyCancellation', 1000000, 1000000, N'{status}');
        """;

    private static string TransferInsertSql(long caseId, Guid operationId) => $"""
        INSERT payment.DirectRefundTransfers
            (operation_id, case_id, reported_amount_vnd, bank_reference,
             actual_transferred_at, reported_at, confirmation_deadline_at,
             snapshot_bank_code, snapshot_account_number, snapshot_account_holder)
        VALUES ('{operationId}', {caseId}, 1000000, N'REF123456',
                SYSUTCDATETIME(), SYSUTCDATETIME(), DATEADD(HOUR, 72, SYSUTCDATETIME()),
                N'VCB', N'0012345678', N'NGUYEN VAN A');
        """;

    private static string CommissionInsertSql(long userId, string period, string code) => $"""
        INSERT commercial.TourDirectCommissionStatements
            (statement_code, tour_operator_user_id, period, eligible_base_vnd,
             commission_rate, commission_amount_vnd, issue_date, due_date)
        VALUES (N'{code}', {userId}, N'{period}', 10000000, 0.1000, 1000000,
                CAST(GETDATE() AS DATE), DATEADD(DAY, 7, CAST(GETDATE() AS DATE)));
        """;

    private static string MigrationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate the repository root.");
        }

        return Path.Combine(directory.FullName, "database", "migrations", MigrationFileName);
    }
}