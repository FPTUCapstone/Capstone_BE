using System.Data;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;

namespace TripMate.Infrastructure.Services;

/// <summary>Retries avatar cleanup while protecting the avatar currently referenced by a user.</summary>
internal sealed class TravelerAvatarCleanupBackgroundService(
    IConfiguration configuration,
    ITravelerAvatarStorage storage,
    IDateTimeProvider clock,
    ILogger<TravelerAvatarCleanupBackgroundService> logger) : BackgroundService
{
    private readonly string connectionString = configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("The default SQL connection is required.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessOneAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Traveler avatar cleanup batch failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task ProcessOneAsync(CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(cancellationToken);
        if (claim is null) return;
        var (id, publicId, token, attempt) = claim.Value;
        await using var lockConnection = new SqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken);
        string resource = TravelerAvatarCleanupLock.ForPublicId(publicId);
        await using (var lockCommand = new SqlCommand("DECLARE @r int; EXEC @r = sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=10000; SELECT @r", lockConnection))
        {
            lockCommand.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
            if ((int)(await lockCommand.ExecuteScalarAsync(cancellationToken) ?? -1) < 0) return;
        }
        try
        {
            if (await IsCurrentReferenceAsync(publicId, cancellationToken))
            {
                await CompleteAsync(id, token, cancellationToken);
                return;
            }
            bool deleted = await storage.DeleteAsync(publicId, cancellationToken);
            if (deleted) await CompleteAsync(id, token, cancellationToken);
            else await RetryAsync(id, token, attempt, cancellationToken);
        }
        finally
        {
            await using var release = new SqlCommand("EXEC sp_releaseapplock @Resource=@resource, @LockOwner='Session'", lockConnection);
            release.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task<(long Id, string PublicId, Guid Token, int Attempt)?> ClaimAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString); await conn.OpenAsync(ct);
        var now = clock.UtcNow;
        var token = Guid.NewGuid();
        await using var cmd = new SqlCommand("""
;WITH due AS (SELECT TOP (1) * FROM dbo.TravelerAvatarCleanupOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
WHERE (cleanup_status='Pending' AND not_before_at<=@now) OR (cleanup_status='Leased' AND lease_expires_at<=@now)
ORDER BY not_before_at, cleanup_id)
UPDATE due SET cleanup_status='Leased', lease_token=@token, lease_expires_at=@lease, attempt_count=attempt_count+1, updated_at=@now
OUTPUT inserted.cleanup_id, inserted.public_id, inserted.attempt_count;
""", conn);
        cmd.Parameters.Add("@now", SqlDbType.DateTime2).Value = now.UtcDateTime;
        cmd.Parameters.Add("@lease", SqlDbType.DateTime2).Value = now.AddMinutes(5).UtcDateTime;
        cmd.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = token;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetInt64(0), reader.GetString(1), token, reader.GetInt32(2)) : null;
    }

    private async Task<bool> IsCurrentReferenceAsync(string publicId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString); await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Users WHERE avatar_public_id=@id) THEN 1 ELSE 0 END", conn);
        cmd.Parameters.Add("@id", SqlDbType.NVarChar, 500).Value = publicId;
        return (int)(await cmd.ExecuteScalarAsync(ct) ?? 0) == 1;
    }

    private async Task CompleteAsync(long id, Guid token, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString); await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE FROM dbo.TravelerAvatarCleanupOutbox WHERE cleanup_id=@id AND cleanup_status='Leased' AND lease_token=@token", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id; cmd.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = token;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task RetryAsync(long id, Guid token, int attempt, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString); await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("UPDATE dbo.TravelerAvatarCleanupOutbox SET cleanup_status='Pending', lease_token=NULL, lease_expires_at=NULL, not_before_at=@next, last_error_code='TRAVELER_AVATAR_CLEANUP_FAILED', updated_at=SYSUTCDATETIME() WHERE cleanup_id=@id AND cleanup_status='Leased' AND lease_token=@token", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id; cmd.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = token;
        cmd.Parameters.Add("@next", SqlDbType.DateTime2).Value = clock.UtcNow.AddMinutes(Math.Min(1 << Math.Min(attempt, 10), 1440)).UtcDateTime;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}