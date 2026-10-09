using System.Data;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

using TripMate.Application.Common.Media;

namespace TripMate.Infrastructure.Persistence;

internal sealed record OperatorDocumentCleanupClaim(
    long Id, string PublicId, string ContentType, string ExpectedReference,
    Guid LeaseToken, int AttemptCount);

/// <summary>
/// Keeps a SQL Server application lock for one document while its Cloudinary deletion is
/// decided and executed. The registration transaction uses the same lock before it commits
/// the document reference.
/// </summary>
internal sealed class OperatorDocumentCleanupDeletionLease : IAsyncDisposable
{
    private readonly SqlConnection connection;
    private readonly OperatorDocumentCleanupClaim claim;
    private readonly string lockResource;

    public OperatorDocumentCleanupDeletionLease(SqlConnection connection,
        OperatorDocumentCleanupClaim claim, string lockResource)
    {
        this.connection = connection;
        this.claim = claim;
        this.lockResource = lockResource;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            DELETE FROM dbo.OperatorDocumentCleanupOutbox
            WHERE cleanup_id = @id AND cleanup_status = 'Leased' AND lease_token = @token
            """, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = claim.Id;
        command.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = claim.LeaseToken;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RetryAsync(DateTimeOffset nextAttemptUtc, string safeErrorCode,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.OperatorDocumentCleanupOutbox
            SET cleanup_status = 'Pending', lease_token = NULL, lease_expires_at = NULL,
                not_before_at = @nextAttempt, last_error_code = @errorCode,
                updated_at = SYSUTCDATETIME()
            WHERE cleanup_id = @id AND cleanup_status = 'Leased' AND lease_token = @token
            """, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = claim.Id;
        command.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = claim.LeaseToken;
        command.Parameters.Add("@nextAttempt", SqlDbType.DateTime2).Value = nextAttemptUtc.UtcDateTime;
        command.Parameters.Add("@errorCode", SqlDbType.VarChar, 100).Value = safeErrorCode;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = new SqlCommand("""
                EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';
                """, connection);
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = lockResource;
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}

/// <summary>
/// Uses its own SQL connection so the reservation survives a later rollback of the
/// registration DbContext transaction. The worker uses the same durable table.
/// </summary>
internal sealed class SqlOperatorDocumentCleanupJournal(IConfiguration configuration)
    : IOperatorDocumentCleanupJournal
{
    private readonly string connectionString = configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("The default SQL connection is required.");

    public async Task ReserveAsync(string publicId, string contentType, DateTimeOffset notBeforeAtUtc,
        CancellationToken cancellationToken)
    {
        string reference = OperatorDocumentReference.Create(publicId, contentType).AbsoluteUri;
        if (publicId.Length > 500 || reference.Length > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(publicId));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            INSERT INTO dbo.OperatorDocumentCleanupOutbox
                (public_id, content_type, expected_reference, not_before_at)
            VALUES (@publicId, @contentType, @reference, @notBefore)
            """, connection);
        command.Parameters.Add("@publicId", SqlDbType.NVarChar, 500).Value = publicId;
        command.Parameters.Add("@contentType", SqlDbType.VarChar, 32).Value = contentType;
        command.Parameters.Add("@reference", SqlDbType.NVarChar, 500).Value = reference;
        command.Parameters.Add("@notBefore", SqlDbType.DateTime2).Value = notBeforeAtUtc.UtcDateTime;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task CompleteAsync(string publicId, CancellationToken cancellationToken) =>
        ExecuteForPublicIdAsync("""
            DELETE FROM dbo.OperatorDocumentCleanupOutbox
            WHERE public_id = @publicId AND cleanup_status = 'Pending'
            """, publicId, cancellationToken);

    public Task RetryNowAsync(string publicId, CancellationToken cancellationToken) =>
        ExecuteForPublicIdAsync("""
            UPDATE dbo.OperatorDocumentCleanupOutbox
            SET not_before_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME()
            WHERE public_id = @publicId AND cleanup_status = 'Pending'
            """, publicId, cancellationToken);

    public async Task<OperatorDocumentCleanupClaim?> ClaimDueAsync(
        DateTimeOffset nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        Guid token = Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            ;WITH due AS (
                SELECT TOP (1) *
                FROM dbo.OperatorDocumentCleanupOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE (cleanup_status = 'Pending' AND not_before_at <= @now)
                   OR (cleanup_status = 'Leased' AND lease_expires_at <= @now)
                ORDER BY not_before_at, cleanup_id
            )
            UPDATE due
            SET cleanup_status = 'Leased', lease_token = @token,
                lease_expires_at = @leaseExpires,
                attempt_count = attempt_count + 1, updated_at = @now
            OUTPUT inserted.cleanup_id, inserted.public_id, inserted.content_type,
                   inserted.expected_reference, inserted.attempt_count;
            """, connection);
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = nowUtc.UtcDateTime;
        command.Parameters.Add("@leaseExpires", SqlDbType.DateTime2).Value =
            (nowUtc + leaseDuration).UtcDateTime;
        command.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = token;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OperatorDocumentCleanupClaim(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), token, reader.GetInt32(4));
    }

    /// <summary>
    /// Acquires the same per-document lock used by the registration transaction. Once the
    /// lock is held, a missing reservation means registration has already won; a persisted
    /// document means a prior commit succeeded but closing its reservation did not.
    /// </summary>
    public async Task<OperatorDocumentCleanupDeletionLease?> AcquireDeletionLeaseAsync(
        OperatorDocumentCleanupClaim claim, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        string lockResource = OperatorDocumentCleanupLock.ForPublicId(claim.PublicId);
        try
        {
            await using (var lockCommand = new SqlCommand("""
                DECLARE @lockResult INT;
                EXEC @lockResult = sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Session',
                    @LockTimeout = 10000;
                SELECT @lockResult;
                """, connection))
            {
                lockCommand.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = lockResource;
                int result = (int)(await lockCommand.ExecuteScalarAsync(cancellationToken) ?? -999);
                if (result < 0)
                {
                    await connection.DisposeAsync();
                    return null;
                }
            }

            await using var ownershipCommand = new SqlCommand("""
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM dbo.OperatorDocumentCleanupOutbox
                    WHERE cleanup_id = @id AND cleanup_status = 'Leased' AND lease_token = @token
                ) THEN 1 ELSE 0 END;
                """, connection);
            ownershipCommand.Parameters.Add("@id", SqlDbType.BigInt).Value = claim.Id;
            ownershipCommand.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = claim.LeaseToken;
            bool ownsClaim = (int)(await ownershipCommand.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
            if (!ownsClaim)
            {
                await ReleaseSessionLockAsync(connection, lockResource, cancellationToken);
                await connection.DisposeAsync();
                return null;
            }

            await using var documentCommand = new SqlCommand("""
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM dbo.OperatorDocuments WHERE file_url = @reference
                ) THEN 1 ELSE 0 END;
                """, connection);
            documentCommand.Parameters.Add("@reference", SqlDbType.NVarChar, 500).Value = claim.ExpectedReference;
            bool registered = (int)(await documentCommand.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
            if (registered)
            {
                await using var completeCommand = new SqlCommand("""
                    DELETE FROM dbo.OperatorDocumentCleanupOutbox
                    WHERE cleanup_id = @id AND cleanup_status = 'Leased' AND lease_token = @token
                    """, connection);
                completeCommand.Parameters.Add("@id", SqlDbType.BigInt).Value = claim.Id;
                completeCommand.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = claim.LeaseToken;
                await completeCommand.ExecuteNonQueryAsync(cancellationToken);
                await ReleaseSessionLockAsync(connection, lockResource, cancellationToken);
                await connection.DisposeAsync();
                return null;
            }

            return new OperatorDocumentCleanupDeletionLease(connection, claim, lockResource);
        }
        catch
        {
            await ReleaseSessionLockAsync(connection, lockResource, CancellationToken.None);
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ReleaseSessionLockAsync(SqlConnection connection, string lockResource,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        await using var command = new SqlCommand("""
            EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';
            """, connection);
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = lockResource;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteForPublicIdAsync(string sql, string publicId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@publicId", SqlDbType.NVarChar, 500).Value = publicId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}