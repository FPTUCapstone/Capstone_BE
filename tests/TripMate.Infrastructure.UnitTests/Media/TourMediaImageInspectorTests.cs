using FluentAssertions;

using SkiaSharp;

using TripMate.Application.Common.Media;
using TripMate.Infrastructure.Media;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class TourMediaImageInspectorTests
{
    [Theory]
    [InlineData("image/jpeg", ".jpg", "image/jpeg")]
    [InlineData("image/png", ".png", "image/png")]
    [InlineData("image/webp", ".webp", "image/webp")]
    public async Task InspectAsync_ValidSupportedImage_ReturnsSanitizedContent(
        string declaredContentType,
        string extension,
        string expectedContentType)
    {
        byte[] bytes = CreateImage(extension);
        string metadataMarker = "SENSITIVE_EMBEDDED_METADATA";
        if (extension == ".jpg")
        {
            bytes = InjectJpegApp1(bytes, metadataMarker);
        }

        var inspector = new SkiaSharpTourMediaImageInspector();

        var result = await inspector.InspectAsync(
            new TourMediaImageSource(
                new MemoryStream(bytes),
                bytes.LongLength,
                $"tour{extension}",
                declaredContentType),
            CancellationToken.None);

        result.IsAccepted.Should().BeTrue();
        result.RejectionReason.Should().BeNull();
        result.Image.Should().NotBeNull();
        result.Image!.ContentType.Should().Be(expectedContentType);
        result.Image.Width.Should().Be(3);
        result.Image.Height.Should().Be(2);
        result.Image.Sha256Hex.Should().MatchRegex("^[0-9A-F]{64}$");

        using var sanitizedData = SKData.CreateCopy(result.Image.Bytes);
        using var sanitized = SKCodec.Create(sanitizedData);
        sanitized.Should().NotBeNull("the sanitized image must remain decodable");
        System.Text.Encoding.UTF8.GetString(result.Image.Bytes)
            .Should().NotContain(metadataMarker, "the image is decoded and re-encoded");
    }

    [Fact]
    public async Task InspectAsync_DeclaredTypeOrExtensionDoesNotMatchDecodedImage_Rejects()
    {
        byte[] bytes = CreateImage(".png");
        var inspector = new SkiaSharpTourMediaImageInspector();

        var result = await inspector.InspectAsync(
            new TourMediaImageSource(
                new MemoryStream(bytes),
                bytes.LongLength,
                "tour.jpg",
                "image/jpeg"),
            CancellationToken.None);

        result.IsAccepted.Should().BeFalse();
        result.RejectionReason.Should().Be(TourMediaImageRejectionReason.MetadataMismatch);
    }

    [Fact]
    public async Task InspectAsync_UnsupportedDecodedFormat_Rejects()
    {
        byte[] bytes = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
        var inspector = new SkiaSharpTourMediaImageInspector();

        var result = await inspector.InspectAsync(
            new TourMediaImageSource(
                new MemoryStream(bytes),
                bytes.LongLength,
                "tour.gif",
                "image/gif"),
            CancellationToken.None);

        result.IsAccepted.Should().BeFalse();
        result.RejectionReason.Should().Be(TourMediaImageRejectionReason.UnsupportedFormat);
    }

    [Fact]
    public async Task InspectAsync_CorruptContent_RejectsWithoutThrowingDecoderDetails()
    {
        var inspector = new SkiaSharpTourMediaImageInspector();
        var bytes = "not-an-image"u8.ToArray();

        var result = await inspector.InspectAsync(
            new TourMediaImageSource(
                new MemoryStream(bytes),
                bytes.LongLength,
                "tour.jpg",
                "image/jpeg"),
            CancellationToken.None);

        result.IsAccepted.Should().BeFalse();
        result.RejectionReason.Should().Be(TourMediaImageRejectionReason.CorruptImage);
        result.SafeErrorCode.Should().Be("TOUR_MEDIA_IMAGE_CORRUPT");
    }

    [Fact]
    public async Task InspectAsync_ValidImageWithTrailingExecutablePayload_RejectsPolyglot()
    {
        byte[] bytes =
        [
            .. CreateImage(".jpg"),
            .. "<script>alert('polyglot')</script>"u8.ToArray(),
        ];
        var inspector = new SkiaSharpTourMediaImageInspector();

        var result = await inspector.InspectAsync(
            new TourMediaImageSource(
                new MemoryStream(bytes),
                bytes.LongLength,
                "tour.jpg",
                "image/jpeg"),
            CancellationToken.None);

        result.IsAccepted.Should().BeFalse();
        result.RejectionReason.Should().Be(TourMediaImageRejectionReason.CorruptImage);
    }

    [Fact]
    public async Task InspectAsync_EncodedLengthExceedsLimit_RejectsBeforeReading()
    {
        var inspector = new SkiaSharpTourMediaImageInspector();
        var stream = new ThrowOnReadStream();

        var result = await inspector.InspectAsync(
            new TourMediaImageSource(
                stream,
                TourMediaImagePolicy.MaxEncodedBytes + 1,
                "tour.png",
                "image/png"),
            CancellationToken.None);

        result.RejectionReason.Should().Be(TourMediaImageRejectionReason.FileTooLarge);
        stream.ReadAttempted.Should().BeFalse();
    }

    [Fact]
    public async Task InspectAsync_StreamExceedsDeclaredAndConfiguredLimit_Rejects()
    {
        var inspector = new SkiaSharpTourMediaImageInspector(
            maxEncodedBytes: 100,
            maxPixelArea: TourMediaImagePolicy.MaxPixelArea);
        byte[] bytes = new byte[101];

        var result = await inspector.InspectAsync(
            new TourMediaImageSource(
                new MemoryStream(bytes),
                100,
                "tour.jpg",
                "image/jpeg"),
            CancellationToken.None);

        result.RejectionReason.Should().Be(TourMediaImageRejectionReason.FileTooLarge);
    }

    [Fact]
    public async Task InspectAsync_DecodedAreaExceedsLimit_Rejects()
    {
        byte[] bytes = CreateImage(".png");
        var inspector = new SkiaSharpTourMediaImageInspector(
            maxEncodedBytes: TourMediaImagePolicy.MaxEncodedBytes,
            maxPixelArea: 5);

        var result = await inspector.InspectAsync(
            new TourMediaImageSource(
                new MemoryStream(bytes),
                bytes.LongLength,
                "tour.png",
                "image/png"),
            CancellationToken.None);

        result.RejectionReason.Should().Be(TourMediaImageRejectionReason.PixelAreaTooLarge);
    }

    [Fact]
    public async Task InspectAsync_Cancelled_PropagatesCancellation()
    {
        byte[] bytes = CreateImage(".png");
        var inspector = new SkiaSharpTourMediaImageInspector();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var action = () => inspector.InspectAsync(
            new TourMediaImageSource(
                new MemoryStream(bytes),
                bytes.LongLength,
                "tour.png",
                "image/png"),
            cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private static byte[] CreateImage(string extension)
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(3, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        SKEncodedImageFormat format = extension switch
        {
            ".jpg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => throw new ArgumentOutOfRangeException(nameof(extension)),
        };
        using SKData data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] InjectJpegApp1(byte[] jpeg, string marker)
    {
        byte[] payload = [.. "Exif\0\0"u8.ToArray(), .. System.Text.Encoding.UTF8.GetBytes(marker)];
        int segmentLength = payload.Length + 2;
        return
        [
            jpeg[0],
            jpeg[1],
            0xFF,
            0xE1,
            (byte)(segmentLength >> 8),
            (byte)segmentLength,
            .. payload,
            .. jpeg[2..],
        ];
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public bool ReadAttempted { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("The stream must not be read.");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}