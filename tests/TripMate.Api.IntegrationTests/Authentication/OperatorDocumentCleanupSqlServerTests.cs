using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Authentication;

public sealed class OperatorDocumentCleanupSqlServerTests
{
    private const string MigrationFileName = "20261008_add_operator_document_cleanup_outbox.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FailedDelete_RemainsInSqlAndSucceedsOnLaterWorkerAttempt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow.AddSeconds(5);
        var journal = Journal(database);
        var storage = new CleanupStorageStub(
            OperatorDocumentStorageDeleteOutcome.TransientFailure,
            OperatorDocumentStorageDeleteOutcome.Deleted);
        var clock = new MutableClock { UtcNow = now };
        var worker = new OperatorDocumentCleanupBackgroundService(
            journal, storage, clock,
            NullLogger<OperatorDocumentCleanupBackgroundService>.Instance);

        await journal.ReserveAsync("operators/retry.pdf", "application/pdf",
            now.AddMinutes(30), CancellationToken.None);
        await journal.RetryNowAsync("operators/retry.pdf", CancellationToken.None);
        (await worker.ProcessDueBatchAsync(CancellationToken.None)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.OperatorDocumentCleanupOutbox
            WHERE public_id = N'operators/retry.pdf' AND cleanup_status = 'Pending'
              AND attempt_count = 1;
            """)).Should().Be(1);

        now = now.AddMinutes(2);
        clock.UtcNow = now;
        (await worker.ProcessDueBatchAsync(CancellationToken.None)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.OperatorDocumentCleanupOutbox;
            """)).Should().Be(0);
        storage.DeleteCount.Should().Be(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CommittedDocument_IsNeverDeletedWhenReservationClosingFailed()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        DateTimeOffset now = new(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);
        var journal = Journal(database);
        const string publicId = "operators/committed.pdf";
        await journal.ReserveAsync(publicId, "application/pdf", now, CancellationToken.None);
        var reference = OperatorDocumentReference.Create(publicId, "application/pdf").AbsoluteUri;
        await database.ExecuteNonQueryAsync($"""
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'cleanup@example.com', N'Cleanup Test', 'PendingApproval');
            DECLARE @userId BIGINT = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@userId, N'Cleanup Test', N'0101234567', N'79-0123/2026/TCDL-GPLHQT');
            INSERT dbo.OperatorDocuments(operator_user_id, document_type, file_url, status)
            VALUES (@userId, 'BusinessLicense', N'{reference}', 'Submitted');
            """);
        var storage = new CleanupStorageStub();
        var clock = new MutableClock { UtcNow = now };
        var worker = new OperatorDocumentCleanupBackgroundService(
            journal, storage, clock,
            NullLogger<OperatorDocumentCleanupBackgroundService>.Instance);

        (await worker.ProcessDueBatchAsync(CancellationToken.None)).Should().Be(1);
        storage.DeleteCount.Should().Be(0);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.OperatorDocumentCleanupOutbox;
            """)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_IsIdempotentAndPreservesReservations()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("DROP TABLE dbo.OperatorDocumentCleanupOutbox;");
        await database.ExecuteScriptAsync(MigrationPath());
        await database.ExecuteScriptAsync(MigrationPath());
        await Journal(database).ReserveAsync("operators/existing.pdf", "application/pdf",
            DateTimeOffset.UtcNow.AddMinutes(30), CancellationToken.None);
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.OperatorDocumentCleanupOutbox
            WHERE public_id = N'operators/existing.pdf';
            """)).Should().Be(1);
    }

    private static SqlOperatorDocumentCleanupJournal Journal(SqlServerTestDatabase database)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = database.ConnectionString,
            })
            .Build();
        return new SqlOperatorDocumentCleanupJournal(configuration);
    }

    private static string MigrationPath() => Path.Combine(
        AppContext.BaseDirectory, "Database", "migrations", MigrationFileName);

    private sealed class MutableClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class CleanupStorageStub(params OperatorDocumentStorageDeleteOutcome[] outcomes)
        : IOperatorDocumentStorage
    {
        private readonly Queue<OperatorDocumentStorageDeleteOutcome> remaining = new(outcomes);

        public int DeleteCount { get; private set; }

        public string AllocatePublicId() => throw new NotSupportedException();

        public Task<OperatorDocumentStorageUploadResult> UploadAsync(
            OperatorDocumentStorageUpload request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperatorDocumentStorageDeleteResult> DeleteAsync(
            string publicId, string contentType, CancellationToken cancellationToken)
        {
            DeleteCount++;
            var outcome = remaining.Dequeue();
            return Task.FromResult(new OperatorDocumentStorageDeleteResult(outcome,
                outcome == OperatorDocumentStorageDeleteOutcome.TransientFailure
                    ? "PROVIDER_UNAVAILABLE" : null));
        }

        public Uri? CreateTemporaryDownloadUrl(string storedReference, DateTimeOffset expiresAtUtc) =>
            throw new NotSupportedException();
    }
}