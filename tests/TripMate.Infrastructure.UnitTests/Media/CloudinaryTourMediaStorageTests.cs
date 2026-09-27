using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Media;
using TripMate.Infrastructure.Media.Cloudinary;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class CloudinaryTourMediaStorageTests
{
    [Fact]
    public void AllocatePublicId_UsesConfiguredTourFolderAndOpaqueIdentifier()
    {
        var storage = CreateStorage(new RecordingCloudinaryClient());

        var publicId = storage.AllocatePublicId(42);

        publicId.Should().MatchRegex(
            "^tripmate/tours/42/[0-9a-f]{32}$");
        publicId.Should().NotContain("filename");
    }

    [Fact]
    public async Task UploadAsync_ForwardsSafeNonOverwritingServerSideRequest()
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = CloudinaryUploadResponse.Succeeded(
                "tripmate/tours/42/2f3df0a78b27490cb8f3266a97882148",
                new Uri("https://res.cloudinary.com/test/image/upload/v1/tour.webp")),
        };
        var storage = CreateStorage(client);
        var request = new TourMediaStorageUpload(
            "tripmate/tours/42/2f3df0a78b27490cb8f3266a97882148",
            "image/webp",
            [1, 2, 3]);

        var result = await storage.UploadAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.DeliveryUrl!.Scheme.Should().Be(Uri.UriSchemeHttps);
        client.LastUpload.Should().NotBeNull();
        client.LastUpload!.PublicId.Should().Be(request.PublicId);
        client.LastUpload.Overwrite.Should().BeFalse();
        client.LastUpload.UseFilename.Should().BeFalse();
        client.LastUpload.UniqueFilename.Should().BeFalse();
        client.LastUpload.DiscardOriginalFilename.Should().BeTrue();
    }

    [Fact]
    public async Task UploadAsync_InsecureProviderUrl_FailsClosed()
    {
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = CloudinaryUploadResponse.Succeeded(
                "tripmate/tours/42/image",
                new Uri("http://res.cloudinary.com/test/image/upload/tour.webp")),
        };
        var storage = CreateStorage(client);

        var result = await storage.UploadAsync(
            new TourMediaStorageUpload(
                "tripmate/tours/42/image",
                "image/webp",
                [1, 2, 3]),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureKind.Should().Be(TourMediaStorageFailureKind.ProviderRejected);
        result.SafeErrorCode.Should().Be("TOUR_MEDIA_STORAGE_INVALID_RESPONSE");
    }

    [Theory]
    [InlineData(2, TourMediaStorageFailureKind.Transient)]
    [InlineData(3, TourMediaStorageFailureKind.ProviderRejected)]
    public async Task UploadAsync_ProviderFailure_MapsToSafeClassification(
        int providerOutcomeValue,
        TourMediaStorageFailureKind expected)
    {
        var outcome = (CloudinaryUploadOutcome)providerOutcomeValue;
        var client = new RecordingCloudinaryClient
        {
            UploadResponse = new CloudinaryUploadResponse(
                outcome,
                null,
                null,
                "SAFE_PROVIDER_CODE"),
        };
        var storage = CreateStorage(client);

        var result = await storage.UploadAsync(
            new TourMediaStorageUpload(
                "tripmate/tours/42/image",
                "image/png",
                [1, 2, 3]),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureKind.Should().Be(expected);
        result.SafeErrorCode.Should().Be("SAFE_PROVIDER_CODE");
    }

    [Fact]
    public async Task UploadAsync_UnexpectedProviderException_RedactsDetails()
    {
        const string sensitive = "sensitive-provider-value raw-provider-body";
        var logger = new CapturingLogger<CloudinaryTourMediaStorage>();
        var storage = CreateStorage(new ThrowingCloudinaryClient(sensitive), logger);

        var result = await storage.UploadAsync(
            new TourMediaStorageUpload(
                "tripmate/tours/42/image",
                "image/png",
                [1, 2, 3]),
            CancellationToken.None);

        result.FailureKind.Should().Be(TourMediaStorageFailureKind.Transient);
        result.SafeErrorCode.Should().Be("TOUR_MEDIA_STORAGE_UNAVAILABLE");
        logger.Messages.Should().NotContain(message => message.Contains(sensitive, StringComparison.Ordinal));
        logger.Messages.Should().NotContain(message => message.Contains("must-not-leak", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, TourMediaStorageDeleteOutcome.Deleted)]
    [InlineData(2, TourMediaStorageDeleteOutcome.AlreadyAbsent)]
    [InlineData(3, TourMediaStorageDeleteOutcome.TransientFailure)]
    [InlineData(4, TourMediaStorageDeleteOutcome.PermanentFailure)]
    public async Task DestroyAsync_AlwaysInvalidatesAndMapsSafeOutcome(
        int providerOutcomeValue,
        TourMediaStorageDeleteOutcome expected)
    {
        var providerOutcome = (CloudinaryDeleteOutcome)providerOutcomeValue;
        var client = new RecordingCloudinaryClient { DeleteOutcome = providerOutcome };
        var storage = CreateStorage(client);

        var result = await storage.DestroyAsync(
            "tripmate/tours/42/image",
            CancellationToken.None);

        result.Outcome.Should().Be(expected);
        client.LastDelete.Should().NotBeNull();
        client.LastDelete!.Invalidate.Should().BeTrue();
    }

    [Fact]
    public async Task UploadAsync_Cancelled_PropagatesCancellation()
    {
        var storage = CreateStorage(new CancellingCloudinaryClient());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var action = () => storage.UploadAsync(
            new TourMediaStorageUpload("tripmate/tours/42/image", "image/png", [1]),
            cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private static CloudinaryTourMediaStorage CreateStorage(
        ICloudinaryClient client,
        ILogger<CloudinaryTourMediaStorage>? logger = null) =>
        new(
            client,
            Options.Create(new CloudinaryOptions
            {
                CloudName = "test-cloud",
                ApiKey = "test-api-key",
                ApiSecret = "test-api-secret",
                TourMediaFolderRoot = "tripmate/tours",
            }),
            logger ?? new CapturingLogger<CloudinaryTourMediaStorage>());

    private sealed class RecordingCloudinaryClient : ICloudinaryClient
    {
        public CloudinaryUploadRequest? LastUpload { get; private set; }
        public CloudinaryDeleteRequest? LastDelete { get; private set; }
        public CloudinaryUploadResponse UploadResponse { get; set; } =
            CloudinaryUploadResponse.TransientFailure("PROVIDER_UNAVAILABLE");
        public CloudinaryDeleteOutcome DeleteOutcome { get; set; } =
            CloudinaryDeleteOutcome.Deleted;

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

    private sealed class ThrowingCloudinaryClient(string message) : ICloudinaryClient
    {
        public Task<CloudinaryUploadResponse> UploadAsync(
            CloudinaryUploadRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);

        public Task<CloudinaryDeleteOutcome> DestroyAsync(
            CloudinaryDeleteRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }

    private sealed class CancellingCloudinaryClient : ICloudinaryClient
    {
        public Task<CloudinaryUploadResponse> UploadAsync(
            CloudinaryUploadRequest request,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<CloudinaryUploadResponse>(cancellationToken);

        public Task<CloudinaryDeleteOutcome> DestroyAsync(
            CloudinaryDeleteRequest request,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<CloudinaryDeleteOutcome>(cancellationToken);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}