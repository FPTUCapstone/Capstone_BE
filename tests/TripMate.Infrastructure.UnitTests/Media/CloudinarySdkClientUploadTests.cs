using FluentAssertions;

using TripMate.Infrastructure.Media.Cloudinary;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class CloudinarySdkClientUploadTests
{
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
    [InlineData(false, CloudinaryDotNet.Actions.ResourceType.Image)]
    [InlineData(true, CloudinaryDotNet.Actions.ResourceType.Raw)]
    public void CreateDeletionParams_SelectsTheRequestedResourceType(
        bool isRaw, CloudinaryDotNet.Actions.ResourceType expected)
    {
        var request = new CloudinaryDeleteRequest("tripmate/operator-documents/opaque-id", true)
        {
            IsRaw = isRaw,
        };

        var deletion = CloudinarySdkClient.CreateDeletionParams(request);

        deletion.PublicId.Should().Be(request.PublicId);
        deletion.Invalidate.Should().BeTrue();
        deletion.ResourceType.Should().Be(expected);
    }
}