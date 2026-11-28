using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.DirectPayment;

public sealed class CommissionPaymentTrackingMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LedgerMigrationRetainsParentsPreventsDuplicateSourcesAndPreservesCreditBalance()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commercial;");
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commerce;");
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE commercial.TourDirectCommissionStatements(statement_id bigint PRIMARY KEY);
            CREATE TABLE commerce.Bookings(booking_id bigint PRIMARY KEY);
            CREATE TABLE payment.PaymentTransactions(transaction_id bigint PRIMARY KEY);
            INSERT commercial.TourDirectCommissionStatements VALUES(1),(2);
            INSERT commerce.Bookings VALUES(1); INSERT payment.PaymentTransactions VALUES(1);
            """);
        var path = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261127_commission_monthly_lines.sql");
        await database.ExecuteScriptAsync(path); await database.ExecuteScriptAsync(path);
        await database.ExecuteNonQueryAsync("""
            INSERT commercial.TourCommissionLedgerEntries(statement_id,booking_id,source_type,source_id,payment_transaction_id,base_amount_vnd,commission_rate,commission_amount_vnd,source_occurred_at,created_at)
            VALUES(1,1,'BookingRevenue',1,1,100000,0.1,10000,SYSUTCDATETIME(),SYSUTCDATETIME());
            INSERT commercial.TourCommissionCalculations(statement_id,gross_revenue_vnd,completed_refunds_vnd,revenue_commission_vnd,refund_commission_credit_vnd,credit_brought_forward_vnd,credit_applied_vnd,credit_carried_forward_vnd,held_booking_count,ambiguous_legacy_booking_count,created_at)
            VALUES(1,10000,100000,1000,10000,50,1000,9050,0,0,SYSUTCDATETIME());
            """);
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteNonQueryAsync("""
            INSERT commercial.TourCommissionLedgerEntries(statement_id,booking_id,source_type,source_id,payment_transaction_id,base_amount_vnd,commission_rate,commission_amount_vnd,source_occurred_at,created_at)
            VALUES(2,1,'BookingRevenue',1,1,100000,0.1,10000,SYSUTCDATETIME(),SYSUTCDATETIME());
            """));
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteNonQueryAsync("UPDATE commercial.TourCommissionCalculations SET credit_carried_forward_vnd=0 WHERE statement_id=1;"));
        Assert.Equal(9050L, await database.ExecuteScalarAsync<long>("SELECT credit_carried_forward_vnd FROM commercial.TourCommissionCalculations WHERE statement_id=1"));
        Assert.Equal(2, await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM commercial.TourDirectCommissionStatements"));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task MigrationIsRepeatableAndEnforcesPaymentCorrelationAndSingleActiveAttempt()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commercial;");
        await database.ExecuteNonQueryAsync("CREATE TABLE commercial.TourDirectCommissionStatements(statement_id bigint PRIMARY KEY); INSERT commercial.TourDirectCommissionStatements VALUES (1),(2);");
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261127_commission_payment_tracking.sql");
        await database.ExecuteScriptAsync(migration); await database.ExecuteScriptAsync(migration);
        await database.ExecuteNonQueryAsync("""
            SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF;
            INSERT commercial.TourCommissionPaymentAttempts(statement_id,order_code,gateway,amount_vnd,status,created_at,updated_at,expires_at)
            VALUES(1,8000000000000001,'PayOS',10000,'Creating',SYSUTCDATETIME(),SYSUTCDATETIME(),DATEADD(minute,15,SYSUTCDATETIME()));
            """);
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteNonQueryAsync("""
            SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF;
            INSERT commercial.TourCommissionPaymentAttempts(statement_id,order_code,gateway,amount_vnd,status,created_at,updated_at,expires_at)
            VALUES(1,8000000000000002,'PayOS',10000,'Pending',SYSUTCDATETIME(),SYSUTCDATETIME(),DATEADD(minute,15,SYSUTCDATETIME()));
            """));
        Assert.Equal(2, await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM commercial.TourDirectCommissionStatements"));
        Assert.Equal(1, await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM commercial.TourCommissionPaymentAttempts"));
    }
}