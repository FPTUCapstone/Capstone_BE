namespace TripMate.Application.Features.Bookings.DirectRefunds.Common;

public sealed record DirectRefundEvidenceUpload(string FileName, string ContentType, byte[] Bytes);
public sealed record StoredDirectRefundEvidence(string StorageObjectKey, string Checksum, long ByteLength);

public interface IDirectRefundEvidenceStorage
{
    Task<StoredDirectRefundEvidence?> StoreAsync(DirectRefundEvidenceUpload upload, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string storageObjectKey, CancellationToken cancellationToken);
    Task<byte[]?> ReadAsync(string storageObjectKey, CancellationToken cancellationToken);
    Uri? CreateTemporaryDownloadUrl(string storageObjectKey, string contentType, DateTimeOffset expiresAtUtc);
}