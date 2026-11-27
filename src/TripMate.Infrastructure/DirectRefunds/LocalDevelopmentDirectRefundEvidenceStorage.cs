using System.Security.Cryptography;

using TripMate.Application.Features.Bookings.DirectRefunds.Common;

namespace TripMate.Infrastructure.DirectRefunds;

/// <summary>
/// Development-only private evidence storage for direct-refund transfer proof.
/// Files are kept outside the web root. Production registrations should use Cloudinary or equivalent.
/// </summary>
internal sealed class LocalDevelopmentDirectRefundEvidenceStorage : IDirectRefundEvidenceStorage
{
    private const string KeyPrefix = "local-dr:";
    private static readonly string Root = Environment.GetEnvironmentVariable("TRIPMATE_DIRECT_REFUND_EVIDENCE_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "tripmate", "direct-refund-evidence");

    public async Task<StoredDirectRefundEvidence?> StoreAsync(
        DirectRefundEvidenceUpload upload, CancellationToken cancellationToken)
    {
        string identifier = Guid.NewGuid().ToString("N");
        string target = Path.Combine(Root, identifier);
        string temporary = target + ".tmp";

        try
        {
            Directory.CreateDirectory(Root);
            await File.WriteAllBytesAsync(temporary, upload.Bytes, cancellationToken);
            File.Move(temporary, target);
            return new StoredDirectRefundEvidence(
                KeyPrefix + identifier,
                Convert.ToHexString(SHA256.HashData(upload.Bytes)),
                upload.Bytes.LongLength);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            TryDelete(temporary);
            return null;
        }
    }

    public Task<bool> DeleteAsync(string storageObjectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetPath(storageObjectKey, out string? target)) return Task.FromResult(false);
        TryDelete(target!);
        return Task.FromResult(true);
    }

    public async Task<byte[]?> ReadAsync(string storageObjectKey, CancellationToken cancellationToken)
    {
        if (!TryGetPath(storageObjectKey, out string? target) || !File.Exists(target!)) return null;
        return await File.ReadAllBytesAsync(target!, cancellationToken);
    }

    public Uri? CreateTemporaryDownloadUrl(string storageObjectKey, string contentType, DateTimeOffset expiresAtUtc) => null;

    private static bool TryGetPath(string storageObjectKey, out string? target)
    {
        target = null;
        if (!storageObjectKey.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        string id = storageObjectKey[KeyPrefix.Length..];
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return false;
        target = Path.Combine(Root, id);
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
