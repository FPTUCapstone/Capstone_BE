using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.Media.Cloudinary;

internal sealed class CloudinarySdkClient : ICloudinaryClient
{
    private const string UnavailableErrorCode = "TOUR_MEDIA_STORAGE_UNAVAILABLE";
    private const string RejectedErrorCode = "TOUR_MEDIA_STORAGE_REJECTED";
    private readonly CloudinaryDotNet.Cloudinary cloudinary;

    public CloudinarySdkClient(IOptions<CloudinaryOptions> options)
    {
        CloudinaryOptions value = options.Value;
        cloudinary = new CloudinaryDotNet.Cloudinary(
            new Account(value.CloudName, value.ApiKey, value.ApiSecret));
        cloudinary.Api.Secure = true;
    }

    public async Task<CloudinaryUploadResponse> UploadAsync(
        CloudinaryUploadRequest request,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(request.Bytes, writable: false);
        ImageUploadParams upload = CreateSignedUploadParams(request, stream);

        ImageUploadResult result;
        try
        {
            result = await cloudinary.UploadAsync(upload, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return CloudinaryUploadResponse.TransientFailure(UnavailableErrorCode);
        }
        catch (TaskCanceledException)
        {
            return CloudinaryUploadResponse.TransientFailure(UnavailableErrorCode);
        }

        if (result.Error is not null)
        {
            int statusCode = (int)result.StatusCode;
            if (statusCode == 429 || statusCode >= 500)
            {
                return CloudinaryUploadResponse.TransientFailure(UnavailableErrorCode);
            }

            // Keep the diagnostic useful without retaining the provider's raw response text.
            string safeErrorCode = statusCode is 400 or 401 or 403
                ? $"{RejectedErrorCode}_HTTP_{statusCode}"
                : RejectedErrorCode;
            return CloudinaryUploadResponse.Rejected(safeErrorCode);
        }

        if (string.IsNullOrWhiteSpace(result.PublicId) || result.SecureUrl is null)
        {
            return CloudinaryUploadResponse.Rejected(RejectedErrorCode);
        }

        return CloudinaryUploadResponse.Succeeded(result.PublicId, result.SecureUrl);
    }

    internal static ImageUploadParams CreateSignedUploadParams(
        CloudinaryUploadRequest request,
        Stream stream) => new()
        {
            File = new FileDescription("tour-media", stream),
            PublicId = request.PublicId,
            Overwrite = request.Overwrite,
            UseFilename = request.UseFilename,
            UniqueFilename = request.UniqueFilename,
            DiscardOriginalFilename = request.DiscardOriginalFilename,
            // Do not set Unsigned=false. The SDK includes an explicitly set false
            // in its signature but Cloudinary omits it from the signed request.
            // Unset means the SDK performs a signed upload by default.
        };

    public async Task<CloudinaryDeleteOutcome> DestroyAsync(
        CloudinaryDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var deletion = new DeletionParams(request.PublicId)
        {
            Invalidate = request.Invalidate,
            ResourceType = ResourceType.Image,
        };

        DeletionResult result;
        try
        {
            result = await cloudinary.DestroyAsync(deletion).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return CloudinaryDeleteOutcome.TransientFailure;
        }
        catch (TaskCanceledException)
        {
            return CloudinaryDeleteOutcome.TransientFailure;
        }

        if (string.Equals(result.Result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return CloudinaryDeleteOutcome.Deleted;
        }

        if (string.Equals(result.Result, "not found", StringComparison.OrdinalIgnoreCase))
        {
            return CloudinaryDeleteOutcome.AlreadyAbsent;
        }

        int statusCode = (int)result.StatusCode;
        return statusCode == 429 || statusCode >= 500
            ? CloudinaryDeleteOutcome.TransientFailure
            : CloudinaryDeleteOutcome.PermanentFailure;
    }
}