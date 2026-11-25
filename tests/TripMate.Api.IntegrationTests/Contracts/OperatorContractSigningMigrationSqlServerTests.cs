using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Contracts;

public sealed class OperatorContractSigningMigrationSqlServerTests
{
    private static string Script => Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261124_operator_contract_signing.sql");

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_seeds_legacy_once_preserves_statuses_and_supports_pending_signature_lock_recovery()
    {
        await using var db = await CreateFoundation();
        await db.ExecuteScriptAsync(Script);
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM admin.OperatorContractLegacyExemptions;"));
        Assert.Equal("Active", await db.ExecuteScalarAsync<string>("SELECT status FROM dbo.Users WHERE user_id=1;"));
        Assert.Equal("Locked", await db.ExecuteScalarAsync<string>("SELECT status FROM dbo.Users WHERE user_id=2;"));
        await db.ExecuteNonQueryAsync("INSERT dbo.Users(user_id,role,status) VALUES(4,'TourOperator','Active'); INSERT dbo.OperatorProfiles VALUES(4,'Approved');");
        await db.ExecuteScriptAsync(Script);
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM admin.OperatorContractLegacyExemptions;"));
        await db.ExecuteNonQueryAsync("UPDATE dbo.Users SET status='PendingContractSignature' WHERE user_id=1; UPDATE dbo.Users SET status='Locked',status_before_lock='PendingContractSignature',locked_at_utc=SYSUTCDATETIME(),lock_reason=N'Test',locked_by_user_id=3 WHERE user_id=1; INSERT admin.UserUnlockOperations(operation_id,restored_status) VALUES(1,'PendingContractSignature');");
        Assert.Equal("PendingContractSignature", await db.ExecuteScalarAsync<string>("SELECT status_before_lock FROM dbo.Users WHERE user_id=1;"));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_prevents_duplicate_current_contract_and_null_evidence_on_signed_contract()
    {
        await using var db = await CreateFoundation();
        await db.ExecuteScriptAsync(Script);
        await InsertContract(db);
        await Assert.ThrowsAsync<SqlException>(() => InsertContract(db));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecuteNonQueryAsync("UPDATE admin.OperatorContracts SET status='Signed' WHERE contract_id=1;"));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecuteNonQueryAsync("UPDATE admin.OperatorContracts SET document_state='Ready' WHERE contract_id=1;"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM admin.OperatorContracts;"));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_rejects_an_incomplete_preexisting_exemption_table_without_partial_contract_tables()
    {
        await using var db = await CreateFoundation();
        await db.ExecuteNonQueryAsync("CREATE TABLE admin.OperatorContractLegacyExemptions(operator_user_id BIGINT PRIMARY KEY);");
        await Assert.ThrowsAsync<SqlException>(() => db.ExecuteScriptAsync(Script));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'admin.OperatorContracts');"));
        Assert.Equal("Active", await db.ExecuteScalarAsync<string>("SELECT status FROM dbo.Users WHERE user_id=1;"));
    }

    private static async Task<SqlServerTestDatabase> CreateFoundation()
    {
        var db = await SqlServerTestDatabase.CreateEmptyAsync();
        await db.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users(user_id BIGINT PRIMARY KEY,role VARCHAR(20),status VARCHAR(24) CHECK(status IN ('Active','Locked','PendingApproval','Rejected','Inactive','PendingEmailVerification')),
              status_before_lock VARCHAR(24),locked_by_user_id BIGINT,locked_at_utc DATETIME2,lock_reason NVARCHAR(1000));
            CREATE TABLE dbo.OperatorProfiles(user_id BIGINT PRIMARY KEY,approval_status VARCHAR(20));
            EXEC('CREATE SCHEMA admin');
            CREATE TABLE admin.OperatorContractTemplates(template_id BIGINT PRIMARY KEY);
            CREATE TABLE admin.UserUnlockOperations(operation_id BIGINT PRIMARY KEY,restored_status VARCHAR(24),CONSTRAINT CK_UserUnlockOperations_RestoredStatus CHECK(restored_status IN ('Active','PendingApproval')));
            INSERT dbo.Users(user_id,role,status) VALUES(1,'TourOperator','Active'),(3,'Staff','Active');
            INSERT dbo.Users(user_id,role,status,status_before_lock,locked_at_utc,lock_reason) VALUES(2,'TourOperator','Locked','Active',SYSUTCDATETIME(),N'Locked');
            INSERT dbo.OperatorProfiles VALUES(1,'Approved'),(2,'Approved');
            INSERT admin.OperatorContractTemplates VALUES(1);
            """);
        return db;
    }

    private static Task InsertContract(SqlServerTestDatabase db) => db.ExecuteNonQueryAsync("""
        INSERT admin.OperatorContracts(operator_user_id,template_id,template_version,template_source_storage_key,template_source_sha256,
            title,company_name,tax_code,business_license_no,business_address,operator_email,signer_name,signer_title,signer_capacity,
            status,document_state,revision,approved_by_user_id,approved_at,created_at,updated_at,preparation_attempts)
        VALUES(1,1,1,N'source',REPLICATE('a',64),N'Test',N'Company',N'000',N'LIC',N'',N'operator@example.invalid',N'',N'',N'',
            'PendingSignature','NeedsSignerDetails',1,3,SYSUTCDATETIME(),SYSUTCDATETIME(),SYSUTCDATETIME(),0);
        """);
}