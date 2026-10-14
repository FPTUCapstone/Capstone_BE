using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripReviews.Media;
using TripMate.Infrastructure.Media.Cloudinary;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class CloudinaryReviewMediaStorageTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private static readonly InspectedReviewImage Image = new([1, 2, 3], "image/png", ".png", 1, 1);
    private sealed class Client : IReviewCloudinaryClient
    {
        public string? Seen;
        public ReviewCloudinaryResponse Response = new(ReviewMediaStorageOutcome.Success, "tripmate/reviews/" + Id, "https://res.cloudinary.com/demo/image/upload/example.png", 3);
        public Exception? Error;
        public Task<ReviewCloudinaryResponse> UploadAsync(string id, InspectedReviewImage image, CancellationToken ct) => Run(id);
        public Task<ReviewCloudinaryResponse> ProbeAsync(string id, CancellationToken ct) => Run(id);
        public Task<ReviewCloudinaryResponse> DestroyAsync(string id, CancellationToken ct) => Run(id);
        private Task<ReviewCloudinaryResponse> Run(string id) { Seen = id; if (Error is not null) throw Error; return Task.FromResult(Response); }
    }
    private static CloudinaryReviewMediaStorage Storage(Client c, string root = "tripmate/reviews") => new(c, Options.Create(new ReviewCloudinaryOptions { ReviewMediaFolderRoot = root }));

    [Fact]
    public async Task Upload_UsesReviewNamespaceAndReturnsValidatedMetadata()
    {
        var client = new Client(); var result = await Storage(client).UploadAsync(Id, Image, default);
        Assert.Equal("tripmate/reviews/" + Id, client.Seen); Assert.Equal(ReviewMediaStorageOutcome.Success, result.Outcome); Assert.Equal(3, result.StoredByteLength);
    }
    [Theory]
    [InlineData("../other")]
    [InlineData("tripmate/tours/id")]
    [InlineData("")]
    [InlineData("https://example.com/a")]
    public async Task RejectsNonOpaqueIdentityBeforeProvider(string id)
    {
        var client = new Client(); await Assert.ThrowsAsync<ArgumentException>(() => Storage(client).DestroyAsync(id, default)); Assert.Null(client.Seen);
    }
    [Theory]
    [InlineData("../reviews")]
    [InlineData("tripmate//reviews")]
    [InlineData("/tripmate/reviews")]
    public async Task RejectsInvalidConfiguredRoot(string root)
    { await Assert.ThrowsAsync<ArgumentException>(() => Storage(new Client(), root).ProbeAsync(Id, default)); }
    [Theory]
    [InlineData("foreign", "https://example.com/a", 3)]
    [InlineData("tripmate/reviews/" + Id, "http://example.com/a", 3)]
    [InlineData("tripmate/reviews/" + Id, "https://example.com/a", 0)]
    public async Task InvalidUploadSuccess_RemainsUnknown(string id, string url, long bytes)
    {
        var c = new Client { Response = new(ReviewMediaStorageOutcome.Success, id, url, bytes) };
        Assert.Equal(ReviewMediaStorageOutcome.UnknownOutcome, (await Storage(c).UploadAsync(Id, Image, default)).Outcome);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadUncertainExceptionsAreSanitized(bool cancel)
    {
        var c = new Client { Error = cancel ? new OperationCanceledException("secret") : new HttpRequestException("secret") };
        var result = await Storage(c).UploadAsync(Id, Image, default); Assert.Equal(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.UnknownOutcome), result);
    }
    [Fact]
    public async Task ProbeAbsent_RemainsAnObservation()
    { var c = new Client { Response = new(ReviewMediaStorageOutcome.AlreadyAbsent) }; Assert.Equal(ReviewMediaStorageOutcome.AlreadyAbsent, (await Storage(c).ProbeAsync(Id, default)).Outcome); }
    [Theory]
    [InlineData("foreign", "https://example.com/a", 3)]
    [InlineData("tripmate/reviews/" + Id, "http://example.com/a", 3)]
    [InlineData("tripmate/reviews/" + Id, "https://user:secret@example.com/a", 3)]
    [InlineData("tripmate/reviews/" + Id, "https://example.com/a", 0)]
    public async Task InvalidProbeSuccess_DoesNotSupplyTerminalUploadEvidence(string id, string url, long bytes)
    {
        var c = new Client { Response = new(ReviewMediaStorageOutcome.Success, id, url, bytes) };
        Assert.Equal(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.TransientFailure),
            await Storage(c).ProbeAsync(Id, default));
    }
    [Fact]
    public async Task CleanupTransportExceptionDoesNotBecomeAbsence()
    {
        var c = new Client { Error = new HttpRequestException("secret") };
        Assert.Equal(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.TransientFailure),
            await Storage(c).DestroyAsync(Id, default));
    }
    [Fact]
    public async Task DestroyUsesExactIdentity()
    { var c = new Client { Response = new(ReviewMediaStorageOutcome.Success) }; Assert.Equal(ReviewMediaStorageOutcome.Success, (await Storage(c).DestroyAsync(Id, default)).Outcome); Assert.Equal("tripmate/reviews/" + Id, c.Seen); }

    [Fact]
    public void SignedUploadHasNoFilenameOrOverwriteSideEffects()
    {
        using var stream = new MemoryStream([1]); var parameters = ReviewCloudinarySdkClient.CreateUpload("tripmate/reviews/" + Id, stream);
        Assert.Equal("tripmate/reviews/" + Id, parameters.PublicId); Assert.False(parameters.Overwrite); Assert.False(parameters.UseFilename);
        Assert.False(parameters.UniqueFilename); Assert.True(parameters.DiscardOriginalFilename); Assert.Null(parameters.Unsigned);
    }
    [Theory]
    [InlineData(400, true, ReviewMediaStorageOutcome.PermanentFailure)]
    [InlineData(401, false, ReviewMediaStorageOutcome.PermanentFailure)]
    [InlineData(429, false, ReviewMediaStorageOutcome.TransientFailure)]
    [InlineData(500, false, ReviewMediaStorageOutcome.TransientFailure)]
    [InlineData(500, true, ReviewMediaStorageOutcome.UnknownOutcome)]
    [InlineData(409, true, ReviewMediaStorageOutcome.UnknownOutcome)]
    [InlineData(401, true, ReviewMediaStorageOutcome.PermanentFailure)]
    [InlineData(403, true, ReviewMediaStorageOutcome.PermanentFailure)]
    [InlineData(429, true, ReviewMediaStorageOutcome.UnknownOutcome)]
    [InlineData(0, true, ReviewMediaStorageOutcome.UnknownOutcome)]
    public void ErrorClassificationDoesNotTurnUncertainUploadIntoNoAsset(int status, bool upload, ReviewMediaStorageOutcome expected)
    { Assert.Equal(expected, ReviewCloudinarySdkClient.ClassifyError(status, upload)); }
}