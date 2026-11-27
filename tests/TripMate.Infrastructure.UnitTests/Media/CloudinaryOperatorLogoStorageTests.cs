using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Media;
using TripMate.Infrastructure.Media.Cloudinary;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class CloudinaryOperatorLogoStorageTests
{
    private const string PublicId = "tripmate/operator-logos/42/2f3df0a78b27490cb8f3266a97882148";

    private sealed class RecordingCloudinaryClient : ICloudinaryClient
    {
        public CloudinaryUploadRequest? LastUpload { get; private set; }
        public CloudinaryDeleteRequest? LastDelete { get; private set; }
        public int UploadCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public Exception? UploadThrows { get; set; }
        public Exception? DeleteThrows { get; set; }
        public CloudinaryUploadResponse UploadResponse { get; set; } =
            CloudinaryUploadResponse.Succeeded(PublicId, new Uri("https://res.cloudinary.com/test/image/upload/v1/logo.png"));
        public CloudinaryDeleteOutcome DeleteOutcome { get; set; } = CloudinaryDeleteOutcome.Deleted;

        public Task<CloudinaryUploadResponse> UploadAsync(CloudinaryUploadRequest request, CancellationToken cancellationToken)
        {
            UploadCalls++;
            LastUpload = request;
            return UploadThrows is null ? Task.FromResult(UploadResponse) : Task.FromException<CloudinaryUploadResponse>(UploadThrows);
        }

        public Task<CloudinaryDeleteOutcome> DestroyAsync(CloudinaryDeleteRequest request, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            LastDelete = request;
            return DeleteThrows is null ? Task.FromResult(DeleteOutcome) : Task.FromException<CloudinaryDeleteOutcome>(DeleteThrows);
        }

        public Uri? CreateTemporaryDownloadUrl(string publicId, string format, bool isRaw, DateTimeOffset expiresAtUtc) =>
            throw new NotSupportedException("Logos are public; no signed download exists.");
    }

    [Fact]
    public void AllocatePublicId_UsesTheOperatorLogoRootAndAnOpaqueIdentifier()
    {
        var storage = Create(new RecordingCloudinaryClient());

        storage.AllocatePublicId(42).Should().MatchRegex("^tripmate/operator-logos/42/[0-9a-f]{32}$");
        storage.AllocatePublicId(42).Should().NotBe(storage.AllocatePublicId(42));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AllocatePublicId_RejectsANonPositiveOperatorId(long operatorId)
    {
        var act = () => Create(new RecordingCloudinaryClient()).AllocatePublicId(operatorId);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task UploadAsync_ForwardsASafeNonOverwritingRequestAndReturnsAnHttpsUrl()
    {
        var client = new RecordingCloudinaryClient();

        var result = await Create(client).UploadAsync(
            new OperatorLogoUpload(PublicId, "image/png", [1, 2, 3]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.DeliveryUrl!.Scheme.Should().Be(Uri.UriSchemeHttps);
        client.LastUpload!.PublicId.Should().Be(PublicId);
        client.LastUpload.ContentType.Should().Be("image/png");
        client.LastUpload.Bytes.Should().Equal(1, 2, 3);
        client.LastUpload.Overwrite.Should().BeFalse();
        client.LastUpload.UseFilename.Should().BeFalse();
        client.LastUpload.UniqueFilename.Should().BeFalse();
        client.LastUpload.DiscardOriginalFilename.Should().BeTrue();
    }

    [Theory]
    [InlineData("tripmate/avatars/42/abc", "image/png", 3)]
    [InlineData("tripmate/operator-logos", "image/png", 3)]
    [InlineData("other/operator-logos/42/abc", "image/png", 3)]
    [InlineData("", "image/png", 3)]
    [InlineData(PublicId, "image/gif", 3)]
    [InlineData(PublicId, "application/pdf", 3)]
    [InlineData(PublicId, "image/png", 0)]
    public async Task UploadAsync_RejectsInvalidInputBeforeCallingTheProvider(string publicId, string contentType, int length)
    {
        var client = new RecordingCloudinaryClient();

        var act = () => Create(client).UploadAsync(
            new OperatorLogoUpload(publicId, contentType, new byte[length]), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        client.UploadCalls.Should().Be(0);
    }

    [Fact]
    public async Task UploadAsync_InsecureProviderUrl_FailsClosed()
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = CloudinaryUploadResponse.Succeeded(PublicId, new Uri("http://res.cloudinary.com/test/logo.png")),
        };

        var result = await Create(client).UploadAsync(new OperatorLogoUpload(PublicId, "image/png", [1]), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.SafeErrorCode.Should().Be("OPERATOR_LOGO_STORAGE_UNAVAILABLE");
    }

    [Fact]
    public async Task UploadAsync_ProviderReturnsADifferentPublicId_FailsClosed()
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = CloudinaryUploadResponse.Succeeded(
                "tripmate/operator-logos/42/other", new Uri("https://res.cloudinary.com/test/logo.png")),
        };

        var result = await Create(client).UploadAsync(new OperatorLogoUpload(PublicId, "image/png", [1]), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UploadAsync_ProviderFailure_ReturnsAStableCodeNotTheProviderMessage()
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = CloudinaryUploadResponse.Rejected("api-key-123 is invalid"),
        };

        var result = await Create(client).UploadAsync(new OperatorLogoUpload(PublicId, "image/png", [1]), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.SafeErrorCode.Should().Be("OPERATOR_LOGO_STORAGE_UNAVAILABLE");
    }

    [Fact]
    public async Task UploadAsync_ProviderThrows_ReturnsFailureInsteadOfThrowing()
    {
        var client = new RecordingCloudinaryClient { UploadThrows = new HttpRequestException("secret detail") };

        var result = await Create(client).UploadAsync(new OperatorLogoUpload(PublicId, "image/png", [1]), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.SafeErrorCode.Should().Be("OPERATOR_LOGO_STORAGE_UNAVAILABLE");
    }

    [Fact]
    public async Task UploadAsync_Cancellation_IsPropagated()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var client = new RecordingCloudinaryClient { UploadThrows = new OperationCanceledException(source.Token) };

        var act = () => Create(client).UploadAsync(new OperatorLogoUpload(PublicId, "image/png", [1]), source.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // The enum is internal, so the theory passes its numeric value: 1 Deleted, 2 AlreadyAbsent,
    // 3 TransientFailure, 4 PermanentFailure.
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public async Task DeleteAsync_ReportsWhetherTheAssetIsGone(int outcome, bool expected)
    {
        var client = new RecordingCloudinaryClient { DeleteOutcome = (CloudinaryDeleteOutcome)outcome };

        var deleted = await Create(client).DeleteAsync(PublicId, CancellationToken.None);

        deleted.Should().Be(expected);
        client.LastDelete!.PublicId.Should().Be(PublicId);
        client.LastDelete.Invalidate.Should().BeTrue();
        client.LastDelete.IsRaw.Should().BeFalse();
        client.LastDelete.IsPrivateDocument.Should().BeFalse();
    }

    [Theory]
    [InlineData("tripmate/avatars/42/abc")]
    [InlineData("tripmate/operator-documents/42/abc")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task DeleteAsync_RefusesAssetsOutsideTheLogoRoot(string publicId)
    {
        var client = new RecordingCloudinaryClient();

        var act = () => Create(client).DeleteAsync(publicId, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        client.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_ProviderThrows_ReturnsFalse()
    {
        var client = new RecordingCloudinaryClient { DeleteThrows = new HttpRequestException("down") };

        (await Create(client).DeleteAsync(PublicId, CancellationToken.None)).Should().BeFalse();
    }

    private static CloudinaryOperatorLogoStorage Create(ICloudinaryClient client) =>
        new(client, NullLogger<CloudinaryOperatorLogoStorage>.Instance);
}