using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Infrastructure.Media.Cloudinary;
using TripMate.Infrastructure.Reporting;

using Xunit;

namespace TripMate.Infrastructure.UnitTests.Reporting;

public sealed class CloudinaryRevenueExportStorageTests
{
    private const string FolderRoot = "tripmate/revenue-exports";
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [Fact]
    public void AllocatePublicId_PlacesFileUnderOperatorScopedFolderWithOpaqueSuffix()
    {
        var storage = CreateStorage(new RecordingCloudinaryClient());

        var publicId = storage.AllocatePublicId(operatorUserId: 101, exportId: 55);

        publicId.Should().MatchRegex($"^{FolderRoot}/101/55-[0-9a-f]{{32}}$");
        publicId.Should().NotContain("filename");
    }

    [Fact]
    public async Task UploadAsync_SuccessfulUpload_UsesRawPrivateNonOverwritingRequest()
    {
        var publicId = $"{FolderRoot}/101/55-opaque";
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = CloudinaryUploadResponse.Succeeded(
                publicId, new Uri($"https://res.cloudinary.com/test/raw/authenticated/v1/{publicId}")),
        };
        var storage = CreateStorage(client);

        var result = await storage.UploadAsync(
            new RevenueExportStorageUpload(publicId, XlsxContentType, [0x50, 0x4b, 0x03, 0x04]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.StorageKey.Should().Be(publicId);
        client.LastUpload!.IsRaw.Should().BeTrue("XLSX is never an image resource type");
        client.LastUpload.IsPrivateDocument.Should().BeTrue();
        client.LastUpload.Overwrite.Should().BeFalse();
        client.LastUpload.DiscardOriginalFilename.Should().BeTrue("tour names are never used as the file path");
    }

    [Fact]
    public async Task UploadAsync_RejectsAProviderResponseForAnotherExport()
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = CloudinaryUploadResponse.Succeeded(
                $"{FolderRoot}/101/999-other",
                new Uri($"https://res.cloudinary.com/test/raw/authenticated/v1/{FolderRoot}/101/999-other")),
        };
        var storage = CreateStorage(client);

        var result = await storage.UploadAsync(
            new RevenueExportStorageUpload($"{FolderRoot}/101/55-opaque", XlsxContentType, [0x01]),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.SafeErrorCode.Should().Be("REVENUE_EXPORT_STORAGE_INVALID_RESPONSE");
    }

    [Theory]
    [InlineData(2, "REVENUE_EXPORT_STORAGE_UNAVAILABLE")]
    [InlineData(3, "REVENUE_EXPORT_STORAGE_REJECTED")]
    public async Task UploadAsync_ProviderFailure_MapsToSafeDefaultWhenProviderGivesNoCode(
        int outcomeValue, string expectedCode)
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = new CloudinaryUploadResponse((CloudinaryUploadOutcome)outcomeValue, null, null, null),
        };
        var storage = CreateStorage(client);

        var result = await storage.UploadAsync(
            new RevenueExportStorageUpload($"{FolderRoot}/101/55-opaque", XlsxContentType, [0x01]),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.SafeErrorCode.Should().Be(expectedCode);
    }

    [Fact]
    public async Task UploadAsync_RejectsAnUnallocatedPublicIdBeforeTheProvider()
    {
        var client = new RecordingCloudinaryClient();
        var storage = CreateStorage(client);

        var act = async () => await storage.UploadAsync(
            new RevenueExportStorageUpload("other-folder/opaque-id", XlsxContentType, [0x01]),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        client.LastUpload.Should().BeNull();
    }

    [Fact]
    public async Task UploadAsync_EmptyBytes_IsRejectedBeforeTheProvider()
    {
        var client = new RecordingCloudinaryClient();
        var storage = CreateStorage(client);

        var act = async () => await storage.UploadAsync(
            new RevenueExportStorageUpload($"{FolderRoot}/101/55-opaque", XlsxContentType, []),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        client.LastUpload.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_UsesRawPrivateInvalidatingRequest()
    {
        var client = new RecordingCloudinaryClient();
        var storage = CreateStorage(client);

        var deleted = await storage.DeleteAsync($"{FolderRoot}/101/55-opaque", CancellationToken.None);

        deleted.Should().BeTrue();
        client.LastDelete!.PublicId.Should().Be($"{FolderRoot}/101/55-opaque");
        client.LastDelete.IsRaw.Should().BeTrue();
        client.LastDelete.IsPrivateDocument.Should().BeTrue();
        client.LastDelete.Invalidate.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_RejectsAnUnallocatedStorageKeyBeforeTheProvider()
    {
        var client = new RecordingCloudinaryClient();
        var storage = CreateStorage(client);

        var act = async () => await storage.DeleteAsync("other-folder/opaque-id", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        client.LastDelete.Should().BeNull();
    }

    [Fact]
    public void CreateTemporaryDownloadUrl_SignsOnlyAllocatedKeysAsRawXlsx()
    {
        var client = new RecordingCloudinaryClient();
        var storage = CreateStorage(client);
        var expiresAt = DateTimeOffset.Parse("2026-10-27T03:00:00Z");

        var url = storage.CreateTemporaryDownloadUrl($"{FolderRoot}/101/55-opaque", expiresAt);

        url!.AbsoluteUri.Should().Be("https://api.cloudinary.com/signed-download");
        client.LastDownload.Should().Be(($"{FolderRoot}/101/55-opaque", "xlsx", true, expiresAt));
    }

    [Theory]
    [InlineData("other-folder/opaque-id")]
    [InlineData("tripmate/revenue-exports/101/../55-opaque")]
    public void CreateTemporaryDownloadUrl_RejectsForeignOrTraversingKeys(string storageKey)
    {
        var storage = CreateStorage(new RecordingCloudinaryClient());

        storage.CreateTemporaryDownloadUrl(storageKey, DateTimeOffset.UtcNow.AddDays(1)).Should().BeNull();
    }

    private static CloudinaryRevenueExportStorage CreateStorage(ICloudinaryClient client) =>
        new(client, Options.Create(new CloudinaryOptions
        {
            CloudName = "test-cloud",
            ApiKey = "api-key-value",
            ApiSecret = "cloud-secret-value",
            TourMediaFolderRoot = "tripmate/tours",
            OperatorDocumentsFolderRoot = "tripmate/operator-documents",
            RevenueExportsFolderRoot = FolderRoot,
        }));

    private sealed class RecordingCloudinaryClient : ICloudinaryClient
    {
        public CloudinaryUploadRequest? LastUpload { get; private set; }
        public CloudinaryUploadResponse UploadResponse { get; set; } =
            CloudinaryUploadResponse.TransientFailure("PROVIDER_UNAVAILABLE");
        public CloudinaryDeleteRequest? LastDelete { get; private set; }
        public CloudinaryDeleteOutcome DeleteOutcome { get; set; } = CloudinaryDeleteOutcome.Deleted;
        public (string PublicId, string Format, bool IsRaw, DateTimeOffset ExpiresAtUtc)? LastDownload { get; private set; }

        public Task<CloudinaryUploadResponse> UploadAsync(
            CloudinaryUploadRequest request, CancellationToken cancellationToken)
        {
            LastUpload = request;
            return Task.FromResult(UploadResponse);
        }

        public Task<CloudinaryDeleteOutcome> DestroyAsync(
            CloudinaryDeleteRequest request, CancellationToken cancellationToken)
        {
            LastDelete = request;
            return Task.FromResult(DeleteOutcome);
        }

        public Uri CreateTemporaryDownloadUrl(string publicId, string format, bool isRaw, DateTimeOffset expiresAtUtc)
        {
            LastDownload = (publicId, format, isRaw, expiresAtUtc);
            return new Uri("https://api.cloudinary.com/signed-download");
        }
    }
}