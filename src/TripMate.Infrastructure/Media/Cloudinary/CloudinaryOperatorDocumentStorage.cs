using Microsoft.Extensions.Options;

using TripMate.Application.Common.Media;

namespace TripMate.Infrastructure.Media.Cloudinary;

internal sealed class CloudinaryOperatorDocumentStorage(
    ICloudinaryClient client,
    IOptions<CloudinaryOptions> options)
    : IOperatorDocumentStorage
{
    private const string InvalidResponseErrorCode = "OPERATOR_DOCUMENT_STORAGE_INVALID_RESPONSE";
    private const string UnavailableErrorCode = "OPERATOR_DOCUMENT_STORAGE_UNAVAILABLE";
    private const string RejectedErrorCode = "OPERATOR_DOCUMENT_STORAGE_REJECTED";

    private static readonly string[] AllowedContentTypes =
        ["application/pdf", "image/jpeg", "image/png"];

    private readonly CloudinaryOptions options = options.Value;

    public string AllocatePublicId() =>
        $"{options.OperatorDocumentsFolderRoot}/{Guid.NewGuid():N}";

    public async Task<OperatorDocumentStorageUploadResult> UploadAsync(
        OperatorDocumentStorageUpload request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PublicId))
        {
            throw new ArgumentException("A public id is required.", nameof(request));
        }

        if (request.Bytes.Length == 0)
        {
            throw new ArgumentException("Document content is required.", nameof(request));
        }

        if (!AllowedContentTypes.Contains(request.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The document content type is unsupported.", nameof(request));
        }

        var uploadRequest = new CloudinaryUploadRequest(
            request.PublicId,
            request.ContentType,
            request.Bytes,
            Overwrite: false,
            UseFilename: false,
            UniqueFilename: false,
            DiscardOriginalFilename: true)
        {
            IsRaw = request.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase),
        };

        CloudinaryUploadResponse response;
        try
        {
            response = await client.UploadAsync(uploadRequest, cancellationToken);
        }
        catch (ArgumentException)
        {
            throw;
        }

        if (response.Outcome == CloudinaryUploadOutcome.Succeeded)
        {
            if (response.SecureUrl is null || !response.SecureUrl.IsAbsoluteUri ||
                !response.SecureUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return OperatorDocumentStorageUploadResult.Failed(
                    TourMediaStorageFailureKind.ProviderRejected,
                    InvalidResponseErrorCode);
            }

            return OperatorDocumentStorageUploadResult.Succeeded(response.SecureUrl);
        }

        return response.Outcome == CloudinaryUploadOutcome.TransientFailure
            ? OperatorDocumentStorageUploadResult.Failed(
                TourMediaStorageFailureKind.Transient,
                response.SafeErrorCode ?? UnavailableErrorCode)
            : OperatorDocumentStorageUploadResult.Failed(
                TourMediaStorageFailureKind.ProviderRejected,
                response.SafeErrorCode ?? RejectedErrorCode);
    }

    public async Task<OperatorDocumentStorageDeleteResult> DeleteAsync(
        string publicId,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicId) ||
            !publicId.StartsWith($"{options.OperatorDocumentsFolderRoot}/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A valid allocated operator document identifier is required.", nameof(publicId));
        }

        if (!AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The document content type is unsupported.", nameof(contentType));
        }

        var outcome = await client.DestroyAsync(
            new CloudinaryDeleteRequest(publicId, Invalidate: true)
            {
                IsRaw = contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase),
            },
            cancellationToken);

        return outcome switch
        {
            CloudinaryDeleteOutcome.Deleted => new(
                OperatorDocumentStorageDeleteOutcome.Deleted, null),
            CloudinaryDeleteOutcome.AlreadyAbsent => new(
                OperatorDocumentStorageDeleteOutcome.AlreadyAbsent, null),
            CloudinaryDeleteOutcome.TransientFailure => new(
                OperatorDocumentStorageDeleteOutcome.TransientFailure, UnavailableErrorCode),
            _ => new(OperatorDocumentStorageDeleteOutcome.PermanentFailure, RejectedErrorCode),
        };
    }
}