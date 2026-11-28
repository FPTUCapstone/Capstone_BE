using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.DirectPayment;

public sealed class StaffComplaintMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task MigrationPreservesOldComplaintsAndEvidenceAndIsRepeatable()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE SCHEMA commerce;
            """);
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (user_id bigint PRIMARY KEY);
            CREATE TABLE commerce.PostTripComplaints (complaint_id bigint PRIMARY KEY, status varchar(32) NOT NULL CONSTRAINT CK_PostTripComplaints_Status CHECK(status IN ('Declined')));
            CREATE TABLE commerce.ComplaintEvidence (evidence_id bigint PRIMARY KEY, complaint_id bigint NOT NULL);
            INSERT dbo.Users VALUES (1);
            INSERT commerce.PostTripComplaints VALUES (10,'Declined');
            INSERT commerce.ComplaintEvidence VALUES (20,10);
            """);
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261127_staff_complaint_resolution.sql");
        await database.ExecuteScriptAsync(migration);
        await database.ExecuteScriptAsync(migration);
        Assert.Equal("Declined", await database.ExecuteScalarAsync<string>("SELECT status FROM commerce.PostTripComplaints WHERE complaint_id=10"));
        Assert.Equal(1, await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM commerce.ComplaintEvidence"));
        await database.ExecuteNonQueryAsync("""
            INSERT commerce.PostTripComplaints VALUES (11,'AwaitingStaffReview');
            INSERT commerce.ComplaintStaffReviewEvents(complaint_id,actor_user_id,operation_id,payload_fingerprint,action,reason,contract_basis,created_at)
            VALUES (11,1,'11111111-1111-4111-8111-111111111111',REPLICATE('A',64),'Reject',N'Contract basis',N'Section 4',SYSUTCDATETIME());
            """);
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteNonQueryAsync("""
            INSERT commerce.ComplaintStaffReviewEvents(complaint_id,actor_user_id,operation_id,payload_fingerprint,action,reason,contract_basis,created_at)
            VALUES (11,1,'11111111-1111-4111-8111-111111111111',REPLICATE('A',64),'Reject',N'Retry',N'Section 4',SYSUTCDATETIME());
            """));
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteNonQueryAsync("UPDATE commerce.PostTripComplaints SET status='Invalid' WHERE complaint_id=11"));
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteNonQueryAsync("UPDATE commerce.ComplaintEvidence SET staff_review_event_id=999 WHERE evidence_id=20"));
    }
}