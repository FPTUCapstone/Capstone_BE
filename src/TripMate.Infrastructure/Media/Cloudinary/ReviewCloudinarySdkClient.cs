using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripReviews.Media;

namespace TripMate.Infrastructure.Media.Cloudinary;

internal sealed class ReviewCloudinarySdkClient(IOptions<ReviewCloudinaryOptions> options) : IReviewCloudinaryClient
{
    private readonly CloudinaryDotNet.Cloudinary client = CreateClient(options.Value);
    private static CloudinaryDotNet.Cloudinary CreateClient(ReviewCloudinaryOptions options)
    {
        var client = new CloudinaryDotNet.Cloudinary(new CloudinaryDotNet.Account(options.CloudName, options.ApiKey, options.ApiSecret));
        client.Api.Secure = true;
        return client;
    }
    internal static ImageUploadParams CreateUpload(string publicId, Stream stream) => new()
    {
        File = new FileDescription("review-media", stream),
        PublicId = publicId,
        Overwrite = false,
        UseFilename = false,
        UniqueFilename = false,
        DiscardOriginalFilename = true,
        // Unset Unsigned uses the SDK's signed upload. Explicit false breaks its signing contract.
    };
    internal static ReviewMediaStorageOutcome ClassifyError(int status, bool uploading)
    {
        if (uploading) return status is 400 or 401 or 403 ? ReviewMediaStorageOutcome.PermanentFailure : ReviewMediaStorageOutcome.UnknownOutcome;
        return status == 429 || status >= 500 || status == 0 ? ReviewMediaStorageOutcome.TransientFailure : ReviewMediaStorageOutcome.PermanentFailure;
    }
    public async Task<ReviewCloudinaryResponse> UploadAsync(string publicId, InspectedReviewImage image, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(image.Bytes, writable: false);
        var result = await client.UploadAsync(CreateUpload(publicId, stream), cancellationToken);
        return result.Error is not null
            ? new(ClassifyError((int)result.StatusCode, uploading: true))
            : new(ReviewMediaStorageOutcome.Success, result.PublicId, result.SecureUrl?.AbsoluteUri, result.Bytes);
    }
    public async Task<ReviewCloudinaryResponse> ProbeAsync(string publicId, CancellationToken cancellationToken)
    {
        var result = await client.GetResourceAsync(new GetResourceParams(publicId) { ResourceType = ResourceType.Image }, cancellationToken);
        if ((int)result.StatusCode == 404) return new(ReviewMediaStorageOutcome.AlreadyAbsent);
        return result.Error is not null
            ? new(ClassifyError((int)result.StatusCode, uploading: false))
            : new(ReviewMediaStorageOutcome.Success, result.PublicId, result.SecureUrl, result.Bytes);
    }
    public async Task<ReviewCloudinaryResponse> DestroyAsync(string publicId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await client.DestroyAsync(new DeletionParams(publicId) { ResourceType = ResourceType.Image, Invalidate = true }).WaitAsync(cancellationToken);
        if (result.Result == "ok") return new(ReviewMediaStorageOutcome.Success);
        if (result.Result == "not found") return new(ReviewMediaStorageOutcome.AlreadyAbsent);
        return new(ClassifyError((int)result.StatusCode, uploading: false));
    }
}