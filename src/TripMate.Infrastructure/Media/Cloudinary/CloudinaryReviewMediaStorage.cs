using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripReviews.Media;

namespace TripMate.Infrastructure.Media.Cloudinary;

public sealed class ReviewCloudinaryOptions
{
    public const string StableReviewMediaFolderRoot = "tripmate/reviews";

    public string CloudName { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string ReviewMediaFolderRoot { get; set; } = StableReviewMediaFolderRoot;
}

internal sealed record ReviewCloudinaryResponse(ReviewMediaStorageOutcome Outcome, string? PublicId = null, string? DeliveryUrl = null, long? Bytes = null);
internal interface IReviewCloudinaryClient
{
    Task<ReviewCloudinaryResponse> UploadAsync(string publicId, InspectedReviewImage image, CancellationToken cancellationToken);
    Task<ReviewCloudinaryResponse> ProbeAsync(string publicId, CancellationToken cancellationToken);
    Task<ReviewCloudinaryResponse> DestroyAsync(string publicId, CancellationToken cancellationToken);
}

internal sealed class CloudinaryReviewMediaStorage(IReviewCloudinaryClient client, IOptions<ReviewCloudinaryOptions> options) : IReviewMediaStorage
{
    public async Task<ReviewMediaStorageResult> UploadAsync(string publicId, InspectedReviewImage image, CancellationToken cancellationToken)
    {
        string identity = Identity(publicId);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Bytes.Length == 0 || image.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
            throw new ArgumentException("Inspected review image required.", nameof(image));
        return await Execute(() => client.UploadAsync(identity, image, cancellationToken), identity, upload: true, metadata: true);
    }
    public Task<ReviewMediaStorageResult> ProbeAsync(string publicId, CancellationToken cancellationToken)
    {
        string identity = Identity(publicId);
        return Execute(() => client.ProbeAsync(identity, cancellationToken), identity, upload: false, metadata: true);
    }
    public Task<ReviewMediaStorageResult> DestroyAsync(string publicId, CancellationToken cancellationToken)
    {
        string identity = Identity(publicId);
        return Execute(() => client.DestroyAsync(identity, cancellationToken), identity, upload: false, metadata: false);
    }
    private string Identity(string id)
    {
        string root = options.Value.ReviewMediaFolderRoot;
        if (string.IsNullOrEmpty(root) || root.Length > 200 || root.Split('/').Any(segment =>
            segment.Length == 0 || segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
            throw new ArgumentException("Invalid review media root.");
        if (id is null || id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Server-generated opaque review identity required.", nameof(id));
        return root + "/" + id;
    }
    private static async Task<ReviewMediaStorageResult> Execute(Func<Task<ReviewCloudinaryResponse>> action, string identity, bool upload, bool metadata)
    {
        try
        {
            ReviewCloudinaryResponse response = await action();
            if (response.Outcome == ReviewMediaStorageOutcome.Success && metadata)
            {
                if (response.PublicId != identity || response.Bytes is null or <= 0 || response.DeliveryUrl?.Length > 2048 ||
                    !Uri.TryCreate(response.DeliveryUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length != 0)
                    return new(upload ? ReviewMediaStorageOutcome.UnknownOutcome : ReviewMediaStorageOutcome.TransientFailure);
                return new(response.Outcome, response.DeliveryUrl, response.Bytes);
            }
            return new(response.Outcome);
        }
        catch (Exception)
        {
            // Never include provider exception text: it can contain credentials or signed URLs.
            return new(upload ? ReviewMediaStorageOutcome.UnknownOutcome : ReviewMediaStorageOutcome.TransientFailure);
        }
    }
}