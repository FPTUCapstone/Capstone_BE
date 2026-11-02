using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutSettlementMigrationSqlServerTests
{
    private const string MigrationFileName = "20261101_uc65_payout_settlement_processing.sql";

    // The catalogue the team verified on 2026-11-01. The migration writes accents as NCHAR(), so
    // comparing against these UTF-8 literals proves the generated code points are right.
    private static readonly (string Bin, string ShortName, string OfficialName)[] ExpectedBanks =
    [
        ("970436", "Vietcombank", "Ngân hàng TMCP Ngoại Thương Việt Nam"),
        ("970415", "VietinBank", "Ngân hàng TMCP Công thương Việt Nam"),
        ("970418", "BIDV", "Ngân hàng TMCP Đầu tư và Phát triển Việt Nam"),
        ("970405", "Agribank", "Ngân hàng Nông nghiệp và Phát triển Nông thôn Việt Nam"),
        ("970407", "Techcombank", "Ngân hàng TMCP Kỹ thương Việt Nam"),
        ("970422", "MBBank", "Ngân hàng TMCP Quân đội"),
        ("970416", "ACB", "Ngân hàng TMCP Á Châu"),
        ("970432", "VPBank", "Ngân hàng TMCP Việt Nam Thịnh Vượng"),
        ("970423", "TPBank", "Ngân hàng TMCP Tiên Phong"),
        ("970403", "Sacombank", "Ngân hàng TMCP Sài Gòn Thương Tín"),
        ("970437", "HDBank", "Ngân hàng TMCP Phát triển Thành phố Hồ Chí Minh"),
        ("970441", "VIB", "Ngân hàng TMCP Quốc tế Việt Nam"),
        ("970443", "SHB", "Ngân hàng TMCP Sài Gòn - Hà Nội"),
        ("970431", "Eximbank", "Ngân hàng TMCP Xuất Nhập khẩu Việt Nam"),
        ("970426", "MSB", "Ngân hàng TMCP Hàng Hải Việt Nam"),
        ("970448", "OCB", "Ngân hàng TMCP Phương Đông"),
        ("970449", "LPBank", "Ngân hàng TMCP Lộc Phát Việt Nam"),
        ("970454", "VietCapitalBank", "Ngân hàng TMCP Bản Việt"),
    ];

    [Fact]
    public async Task MigrationScript_IsAsciiOnlyAndIdempotentlyGuarded()
    {
        var script = await File.ReadAllTextAsync(MigrationPath());

        script.ToCharArray().Should().OnlyContain(
            character => character < 128,
            "accents are written as NCHAR() so the code page of sqlcmd cannot corrupt them");
        script.Should().Contain("IF OBJECT_ID(N'payment.Banks', N'U') IS NULL");
        script.Should().Contain("IF COL_LENGTH(N'payment.Payouts', N'bank_bin') IS NULL");
        script.Should().Contain("IF OBJECT_ID(N'payment.PayoutSettlementWorkItems', N'U') IS NULL");
        script.Should().Contain("COMMIT TRANSACTION");
        script.Should().NotContain("CK_Payouts_LifecycleState", "UC-65 does not change the existing lifecycle constraints");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_CreatesCatalogueColumnAndWorkItemTableAndCanRunAgain()
    {
        await using var database = await CreateDatabaseAsync();

        await database.ExecuteScriptAsync(MigrationPath());
        await AssertShapeAsync(database);

        await database.ExecuteScriptAsync(MigrationPath());
        await AssertShapeAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_SeedsExactlyTheVerifiedCatalogue()
    {
        await using var database = await CreateDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        var actual = new List<(string Bin, string ShortName, string OfficialName)>();
        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT bank_bin, short_name, official_name FROM payment.Banks ORDER BY bank_bin;", connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                actual.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        actual.Should().BeEquivalentTo(ExpectedBanks);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_EnforcesBinFormatOneWorkItemPerPayoutAndKnownStates()
    {
        await using var database = await CreateDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());
        await database.ExecuteNonQueryAsync("INSERT payment.Payouts(status) VALUES ('SettlementProcessing');");

        Func<Task> badPayoutBin = () => database.ExecuteNonQueryAsync(
            "UPDATE payment.Payouts SET bank_bin = '12AB56' WHERE payout_id = 1;");
        await badPayoutBin.Should().ThrowAsync<SqlException>();
        Func<Task> shortPayoutBin = () => database.ExecuteNonQueryAsync(
            "UPDATE payment.Payouts SET bank_bin = '97043' WHERE payout_id = 1;");
        await shortPayoutBin.Should().ThrowAsync<SqlException>();
        await database.ExecuteNonQueryAsync("UPDATE payment.Payouts SET bank_bin = '970436' WHERE payout_id = 1;");

        Func<Task> badCatalogueBin = () => database.ExecuteNonQueryAsync(
            "INSERT payment.Banks(bank_bin, short_name, official_name) VALUES ('9704X6', N'X', N'X bank');");
        await badCatalogueBin.Should().ThrowAsync<SqlException>();

        await database.ExecuteNonQueryAsync("""
            INSERT payment.PayoutSettlementWorkItems(payout_id, state, created_at)
            VALUES (1, 'Pending', SYSUTCDATETIME());
            """);
        Func<Task> secondItem = () => database.ExecuteNonQueryAsync("""
            INSERT payment.PayoutSettlementWorkItems(payout_id, state, created_at)
            VALUES (1, 'Pending', SYSUTCDATETIME());
            """);
        await secondItem.Should().ThrowAsync<SqlException>("a payout may have only one durable work item");

        await database.ExecuteNonQueryAsync("INSERT payment.Payouts(status) VALUES ('SettlementProcessing');");
        Func<Task> unknownState = () => database.ExecuteNonQueryAsync("""
            INSERT payment.PayoutSettlementWorkItems(payout_id, state, created_at)
            VALUES (2, 'Unknown', SYSUTCDATETIME());
            """);
        await unknownState.Should().ThrowAsync<SqlException>();
        Func<Task> negativeAttempts = () => database.ExecuteNonQueryAsync("""
            INSERT payment.PayoutSettlementWorkItems(payout_id, state, attempt_count, created_at)
            VALUES (2, 'Pending', -1, SYSUTCDATETIME());
            """);
        await negativeAttempts.Should().ThrowAsync<SqlException>();
    }

    private static async Task<SqlServerTestDatabase> CreateDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE payment.Payouts (
                payout_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Payouts PRIMARY KEY,
                status VARCHAR(24) NOT NULL
            );
            """);
        return database;
    }

    private static async Task AssertShapeAsync(SqlServerTestDatabase database)
    {
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payment.Banks;")).Should().Be(18);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.Payouts') AND name = N'bank_bin'
              AND max_length = 6 AND is_nullable = 1 AND system_type_id = TYPE_ID(N'char');
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.Payouts') AND name = N'CK_Payouts_BankBin';
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.Banks') AND name = N'CK_Banks_Bin';
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.PayoutSettlementWorkItems')
              AND name IN (N'work_item_id', N'payout_id', N'state', N'attempt_count', N'next_attempt_at',
                           N'lease_token', N'lease_expires_at', N'gateway_reference', N'submitted_at',
                           N'poll_deadline_at', N'poll_count', N'last_polled_at', N'last_error_code',
                           N'created_at', N'completed_at', N'row_version');
            """)).Should().Be(16);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'payment.PayoutSettlementWorkItems')
              AND name = N'IX_PayoutSettlementWorkItems_Due';
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.key_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.PayoutSettlementWorkItems')
              AND name = N'UX_PayoutSettlementWorkItems_Payout' AND type = 'UQ';
            """)).Should().Be(1);
    }

    private static string MigrationPath() => Path.Combine(
        AppContext.BaseDirectory, "Database", "migrations", MigrationFileName);
}