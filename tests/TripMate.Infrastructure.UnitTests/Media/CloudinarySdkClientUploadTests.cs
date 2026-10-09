using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Infrastructure.Media.Cloudinary;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class CloudinarySdkClientUploadTests
{
    [Theory]
    [InlineData(false, "image")]
    [InlineData(true, "raw")]
    public void CreateTemporaryDownloadUrl_UsesAuthenticatedAssetAndShortExpiry(
        bool isRaw, string resourceType)
    {
        var client = new CloudinarySdkClient(Options.Create(new CloudinaryOptions
        {
            CloudName = "test-cloud",
            ApiKey = "test-api-key",
            ApiSecret = "test-api-secret",
            TourMediaFolderRoot = "tripmate/tours",
            OperatorDocumentsFolderRoot = "tripmate/operator-documents",
        }));
        var expiry = DateTimeOffset.Parse("2026-10-08T12:05:00Z");

        var url = client.CreateTemporaryDownloadUrl(
            "tripmate/operator-documents/opaque-id", isRaw ? "pdf" : "png", isRaw, expiry);

        url.Should().NotBeNull();
        url!.Scheme.Should().Be(Uri.UriSchemeHttps);
        url.AbsolutePath.Should().Contain($"/{resourceType}/download");
        url.Query.Should().Contain("type=authenticated");
        url.Query.Should().Contain($"expires_at={expiry.ToUnixTimeSeconds()}");
        url.Query.Should().Contain("signature=");
    }

    [Fact]
    public void CreateSignedUploadParams_OmitsUnsignedAndPreservesPrivateNonOverwritingShape()
    {
        const string publicId = "tripmate/tests/tours/42/opaque-image";
        using var stream = new MemoryStream([1, 2, 3]);
        var request = new CloudinaryUploadRequest(publicId, "image/png", [1, 2, 3],
            Overwrite: false, UseFilename: false, UniqueFilename: false,
            DiscardOriginalFilename: true);

        var upload = CloudinarySdkClient.CreateSignedUploadParams(request, stream);
        var fields = upload.ToParamsDictionary();

        upload.File.FileName.Should().Be("tour-media");
        upload.Unsigned.Should().BeNull();
        fields.Should().NotContainKey("unsigned");
        fields["public_id"].Should().Be(publicId);
        fields["overwrite"].Should().Be("false");
        fields["use_filename"].Should().Be("false");
        fields["discard_original_filename"].Should().Be("true");
        fields.Should().NotContainKey("unique_filename");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateSignedUploadParams_AuthenticatesOnlyOperatorDocuments(bool isRaw)
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var request = new CloudinaryUploadRequest("tripmate/operator-documents/opaque", "image/png",
            [1, 2, 3], false, false, false, true)
        {
            IsRaw = isRaw,
            IsPrivateDocument = true,
        };

        var fields = isRaw
            ? CloudinarySdkClient.CreateSignedRawUploadParams(request, stream).ToParamsDictionary()
            : CloudinarySdkClient.CreateSignedUploadParams(request, stream).ToParamsDictionary();
        fields["type"].Should().Be("authenticated");
    }

    [Theory]
    [InlineData(false, CloudinaryDotNet.Actions.ResourceType.Image)]
    [InlineData(true, CloudinaryDotNet.Actions.ResourceType.Raw)]
    public void CreateDeletionParams_SelectsTheRequestedResourceType(
        bool isRaw, CloudinaryDotNet.Actions.ResourceType expected)
    {
        var request = new CloudinaryDeleteRequest("tripmate/operator-documents/opaque-id", true)
        {
            IsRaw = isRaw,
            IsPrivateDocument = true,
        };

        var deletion = CloudinarySdkClient.CreateDeletionParams(request);

        deletion.PublicId.Should().Be(request.PublicId);
        deletion.Invalidate.Should().BeTrue();
        deletion.ResourceType.Should().Be(expected);
        deletion.Type.Should().Be("authenticated");
    }
}