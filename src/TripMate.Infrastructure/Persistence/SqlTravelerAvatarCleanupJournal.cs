using System.Data;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

using TripMate.Application.Common.Media;

namespace TripMate.Infrastructure.Persistence;

/// <summary>
/// Persists avatar cleanup intent on an independent SQL connection so it survives a failed
/// profile transaction after Cloudinary has accepted an upload.
/// </summary>
internal sealed class SqlTravelerAvatarCleanupJournal(IConfiguration configuration)
    : ITravelerAvatarCleanupJournal
{
    private readonly string connectionString = configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("The default SQL connection is required.");

    public async Task ReserveAsync(
        string publicId,
        DateTimeOffset notBeforeAtUtc,
        CancellationToken cancellationToken)
    {
        ValidatePublicId(publicId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
MERGE dbo.TravelerAvatarCleanupOutbox WITH (HOLDLOCK) AS target
USING (SELECT @publicId AS public_id) AS source ON target.public_id = source.public_id
WHEN MATCHED AND target.cleanup_status = 'Pending' THEN
    UPDATE SET not_before_at = CASE WHEN target.not_before_at > @notBefore THEN @notBefore ELSE target.not_before_at END,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (public_id, not_before_at) VALUES (@publicId, @notBefore);
""", connection);
        command.Parameters.Add("@publicId", SqlDbType.NVarChar, 500).Value = publicId;
        command.Parameters.Add("@notBefore", SqlDbType.DateTime2).Value = notBeforeAtUtc.UtcDateTime;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task CompleteAsync(string publicId, CancellationToken cancellationToken) =>
        ExecuteAsync("DELETE FROM dbo.TravelerAvatarCleanupOutbox WHERE public_id = @publicId AND cleanup_status = 'Pending'", publicId, cancellationToken);

    public Task RetryNowAsync(string publicId, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE dbo.TravelerAvatarCleanupOutbox SET not_before_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE public_id = @publicId AND cleanup_status = 'Pending'", publicId, cancellationToken);

    private async Task ExecuteAsync(string sql, string publicId, CancellationToken cancellationToken)
    {
        ValidatePublicId(publicId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@publicId", SqlDbType.NVarChar, 500).Value = publicId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static void ValidatePublicId(string publicId)
    {
        if (string.IsNullOrWhiteSpace(publicId) || publicId.Length > 500 ||
            !publicId.StartsWith("tripmate/avatars/", StringComparison.Ordinal))
            throw new ArgumentException("A valid Traveler avatar public identifier is required.", nameof(publicId));
    }
}