using System.Text;

using FluentAssertions;

using TripMate.Application.Common.Media;
using TripMate.Infrastructure.Media.Cloudinary;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class CloudinaryOperatorDocumentStorageTests
{
    private const string FolderRoot = "tripmate/operator-documents";

    private sealed class RecordingCloudinaryClient : ICloudinaryClient
    {
        public CloudinaryUploadRequest? LastUpload { get; private set; }
        public CloudinaryUploadResponse UploadResponse { get; set; } =
            CloudinaryUploadResponse.TransientFailure("PROVIDER_UNAVAILABLE");
        public CloudinaryDeleteRequest? LastDelete { get; private set; }
        public CloudinaryDeleteOutcome DeleteOutcome { get; set; } = CloudinaryDeleteOutcome.Deleted;

        public Task<CloudinaryUploadResponse> UploadAsync(
            CloudinaryUploadRequest request,
            CancellationToken cancellationToken)
        {
            LastUpload = request;
            return Task.FromResult(UploadResponse);
        }

        public Task<CloudinaryDeleteOutcome> DestroyAsync(
            CloudinaryDeleteRequest request,
            CancellationToken cancellationToken)
        {
            LastDelete = request;
            return Task.FromResult(DeleteOutcome);
        }
    }

    private static CloudinaryOptions ValidOptions() => new()
    {
        CloudName = "test-cloud",
        ApiKey = "api-key-value",
        ApiSecret = "cloud-secret-value",
        TourMediaFolderRoot = "tripmate/tours",
        OperatorDocumentsFolderRoot = FolderRoot,
    };

    private static OperatorDocumentStorageUpload ValidPdfUpload() =>
        new("public-id-1", "application/pdf", [0x25, 0x50, 0x44, 0x46]);

    [Fact]
    public void AllocatePublicId_PlacesDocumentUnderTheConfiguredFolder()
    {
        var storage = new CloudinaryOperatorDocumentStorage(
            new RecordingCloudinaryClient(),
            Microsoft.Extensions.Options.Options.Create(ValidOptions()));

        var publicId = storage.AllocatePublicId();

        publicId.Should().StartWith($"{FolderRoot}/");
        publicId.Substring(FolderRoot.Length + 1).Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task UploadAsync_SuccessfulPdf_UsesRawResourceAndReturnsHttpsUrl()
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = CloudinaryUploadResponse.Succeeded(
                "public-id-1", new Uri("https://res.cloudinary.com/test/file.pdf")),
        };
        var storage = new CloudinaryOperatorDocumentStorage(
            client, Microsoft.Extensions.Options.Options.Create(ValidOptions()));

        var result = await storage.UploadAsync(ValidPdfUpload(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.DeliveryUrl!.Scheme.Should().Be(Uri.UriSchemeHttps);
        client.LastUpload!.IsRaw.Should().BeTrue("PDFs upload through the raw resource type");
        client.LastUpload.Overwrite.Should().BeFalse();
    }

    [Fact]
    public async Task UploadAsync_SuccessfulImage_UsesImageResourceType()
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = CloudinaryUploadResponse.Succeeded(
                "public-id-2", new Uri("https://res.cloudinary.com/test/file.png")),
        };
        var storage = new CloudinaryOperatorDocumentStorage(
            client, Microsoft.Extensions.Options.Options.Create(ValidOptions()));

        var result = await storage.UploadAsync(
            new OperatorDocumentStorageUpload("public-id-2", "image/png", [0x89, 0x50]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        client.LastUpload!.IsRaw.Should().BeFalse();
    }

    [Fact]
    public async Task UploadAsync_InsecureProviderUrl_FailsClosed()
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = CloudinaryUploadResponse.Succeeded(
                "public-id-1", new Uri("http://res.cloudinary.com/test/file.pdf")),
        };
        var storage = new CloudinaryOperatorDocumentStorage(
            client, Microsoft.Extensions.Options.Options.Create(ValidOptions()));

        var result = await storage.UploadAsync(ValidPdfUpload(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureKind.Should().Be(TourMediaStorageFailureKind.ProviderRejected);
        result.SafeErrorCode.Should().Be("OPERATOR_DOCUMENT_STORAGE_INVALID_RESPONSE");
    }

    [Theory]
    [InlineData(2, TourMediaStorageFailureKind.Transient, "PROVIDER_UNAVAILABLE")]
    [InlineData(3, TourMediaStorageFailureKind.ProviderRejected, "PROVIDER_REJECTED")]
    public async Task UploadAsync_ProviderOutcome_MapsToSafeClassification(
        int outcome, TourMediaStorageFailureKind failureKind, string safeErrorCode)
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = new CloudinaryUploadResponse(
                (CloudinaryUploadOutcome)outcome, null, null, safeErrorCode),
        };
        var storage = new CloudinaryOperatorDocumentStorage(
            client, Microsoft.Extensions.Options.Options.Create(ValidOptions()));

        var result = await storage.UploadAsync(ValidPdfUpload(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureKind.Should().Be(failureKind);
        result.SafeErrorCode.Should().Be(safeErrorCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/zip")]
    public async Task UploadAsync_UnsupportedContentType_IsRejectedBeforeTheProvider(
        string contentType)
    {
        var client = new RecordingCloudinaryClient();
        var storage = new CloudinaryOperatorDocumentStorage(
            client, Microsoft.Extensions.Options.Options.Create(ValidOptions()));

        var act = async () => await storage.UploadAsync(
            new OperatorDocumentStorageUpload("public-id-1", contentType, [0x01]),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        client.LastUpload.Should().BeNull();
    }

    [Fact]
    public async Task UploadAsync_EmptyBytes_IsRejectedBeforeTheProvider()
    {
        var client = new RecordingCloudinaryClient();
        var storage = new CloudinaryOperatorDocumentStorage(
            client, Microsoft.Extensions.Options.Options.Create(ValidOptions()));

        var act = async () => await storage.UploadAsync(
            new OperatorDocumentStorageUpload("public-id-1", "application/pdf", []),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        client.LastUpload.Should().BeNull();
    }

    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("image/jpeg", false)]
    [InlineData("image/png", false)]
    public async Task DeleteAsync_UsesMatchingResourceTypeAndInvalidates(
        string contentType, bool isRaw)
    {
        var client = new RecordingCloudinaryClient();
        var storage = new CloudinaryOperatorDocumentStorage(
            client, Microsoft.Extensions.Options.Options.Create(ValidOptions()));

        var result = await storage.DeleteAsync(
            $"{FolderRoot}/opaque-id", contentType, CancellationToken.None);

        result.Outcome.Should().Be(OperatorDocumentStorageDeleteOutcome.Deleted);
        client.LastDelete.Should().NotBeNull();
        client.LastDelete!.PublicId.Should().Be($"{FolderRoot}/opaque-id");
        client.LastDelete.IsRaw.Should().Be(isRaw);
        client.LastDelete.Invalidate.Should().BeTrue();
    }

    [Theory]
    [InlineData(2, OperatorDocumentStorageDeleteOutcome.AlreadyAbsent)]
    [InlineData(3, OperatorDocumentStorageDeleteOutcome.TransientFailure)]
    [InlineData(4, OperatorDocumentStorageDeleteOutcome.PermanentFailure)]
    public async Task DeleteAsync_MapsProviderOutcome(
        int providerOutcome, OperatorDocumentStorageDeleteOutcome expected)
    {
        var client = new RecordingCloudinaryClient
        {
            DeleteOutcome = (CloudinaryDeleteOutcome)providerOutcome,
        };
        var storage = new CloudinaryOperatorDocumentStorage(
            client, Microsoft.Extensions.Options.Options.Create(ValidOptions()));

        var result = await storage.DeleteAsync(
            $"{FolderRoot}/opaque-id", "application/pdf", CancellationToken.None);

        result.Outcome.Should().Be(expected);
        result.SafeErrorCode.Should().Be(expected switch
        {
            OperatorDocumentStorageDeleteOutcome.AlreadyAbsent => null,
            OperatorDocumentStorageDeleteOutcome.TransientFailure => "OPERATOR_DOCUMENT_STORAGE_UNAVAILABLE",
            _ => "OPERATOR_DOCUMENT_STORAGE_REJECTED",
        });
    }

    [Theory]
    [InlineData("other-folder/opaque-id", "application/pdf")]
    [InlineData("tripmate/operator-documents/opaque-id", "text/plain")]
    public async Task DeleteAsync_RejectsInvalidRequestBeforeProvider(
        string publicId, string contentType)
    {
        var client = new RecordingCloudinaryClient();
        var storage = new CloudinaryOperatorDocumentStorage(
            client, Microsoft.Extensions.Options.Options.Create(ValidOptions()));

        var action = () => storage.DeleteAsync(publicId, contentType, CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentException>();
        client.LastDelete.Should().BeNull();
    }
}