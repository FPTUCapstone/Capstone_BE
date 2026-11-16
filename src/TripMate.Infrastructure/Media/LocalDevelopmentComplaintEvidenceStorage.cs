using System.Security.Cryptography;

using TripMate.Application.Features.Traveler.PostTripComplaints.Common;

namespace TripMate.Infrastructure.Media;

/// <summary>
/// Development-only private evidence storage. Files are kept outside the web root and are
/// intentionally not served by an HTTP endpoint. Production registrations use Cloudinary.
/// </summary>
internal sealed class LocalDevelopmentComplaintEvidenceStorage : IComplaintEvidenceStorage
{
    private const string KeyPrefix = "local:";
    private static readonly string Root = Environment.GetEnvironmentVariable("TRIPMATE_COMPLAINT_EVIDENCE_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "tripmate", "complaint-evidence");

    public async Task<StoredComplaintEvidence?> StoreAsync(
        ComplaintEvidenceUpload upload, CancellationToken cancellationToken)
    {
        string identifier = Guid.NewGuid().ToString("N");
        string target = Path.Combine(Root, identifier);
        string temporary = target + ".tmp";

        try
        {
            Directory.CreateDirectory(Root);
            await File.WriteAllBytesAsync(temporary, upload.Bytes, cancellationToken);
            File.Move(temporary, target);
            return new StoredComplaintEvidence(
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
        if (!storageObjectKey.StartsWith(KeyPrefix, StringComparison.Ordinal) ||
            storageObjectKey.Length != KeyPrefix.Length + 32 ||
            !storageObjectKey[KeyPrefix.Length..].All(Uri.IsHexDigit))
        {
            return Task.FromResult(false);
        }

        string target = Path.Combine(Root, storageObjectKey[KeyPrefix.Length..]);
        TryDelete(target);
        return Task.FromResult(true);
    }

    public async Task<byte[]?> ReadAsync(string storageObjectKey, CancellationToken cancellationToken)
    {
        if (!TryGetPath(storageObjectKey, out string? target) || !File.Exists(target)) return null;
        return await File.ReadAllBytesAsync(target, cancellationToken);
    }

    public Uri? CreateTemporaryDownloadUrl(string storageObjectKey, string contentType, DateTimeOffset expiresAtUtc) => null;

    private static bool TryGetPath(string storageObjectKey, out string? target)
    {
        target = null;
        if (!storageObjectKey.StartsWith(KeyPrefix, StringComparison.Ordinal) || storageObjectKey.Length != KeyPrefix.Length + 32 || !storageObjectKey[KeyPrefix.Length..].All(Uri.IsHexDigit)) return false;
        target = Path.Combine(Root, storageObjectKey[KeyPrefix.Length..]);
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}