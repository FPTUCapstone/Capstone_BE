namespace TripMate.Application.Features.Traveler.PostTripComplaints.Common;

public sealed record ComplaintEvidenceUpload(string FileName, string ContentType, byte[] Bytes);
public sealed record StoredComplaintEvidence(string StorageObjectKey, string Checksum, long ByteLength);

public interface IComplaintEvidenceStorage
{
    Task<StoredComplaintEvidence?> StoreAsync(ComplaintEvidenceUpload upload, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string storageObjectKey, CancellationToken cancellationToken);
    Task<byte[]?> ReadAsync(string storageObjectKey, CancellationToken cancellationToken);
    Uri? CreateTemporaryDownloadUrl(string storageObjectKey, string contentType, DateTimeOffset expiresAtUtc);
}