using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.DirectPayment;

public sealed class TravelerRefundAccountMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task MigrationIsRepeatablePreservesUsersAndEnforcesOneOwnedDefault()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE TABLE dbo.Users (user_id BIGINT NOT NULL PRIMARY KEY); INSERT dbo.Users VALUES (1);");
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261127_traveler_refund_accounts.sql");
        await database.ExecuteScriptAsync(migration);
        await database.ExecuteScriptAsync(migration);
        Assert.Equal(1, await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Users"));
        await database.ExecuteNonQueryAsync("INSERT payment.TravelerRefundAccounts VALUES (1, N'VCB', N'protected-account', N'protected-holder', SYSUTCDATETIME())");
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteNonQueryAsync("INSERT payment.TravelerRefundAccounts VALUES (1, N'VCB', N'protected-account', N'protected-holder', SYSUTCDATETIME())"));
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteNonQueryAsync("INSERT payment.TravelerRefundAccounts VALUES (2, N'VCB', N'protected-account', N'protected-holder', SYSUTCDATETIME())"));
        Assert.Equal(1, await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payment.TravelerRefundAccounts"));
    }
}