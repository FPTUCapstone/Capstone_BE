using System.Buffers.Binary;

using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using SkiaSharp;

using TripMate.Application.Features.TripReviews.Media;
using TripMate.Infrastructure.Reviews.Media;

namespace TripMate.Infrastructure.UnitTests.Reviews;

public sealed class ReviewImageInspectorTests
{
    [Fact]
    public void InfrastructureRegistersInspector()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IReviewImageInspector>().Should().BeOfType<ReviewImageInspector>();
    }

    private static Task<ReviewImageInspection> Inspect(byte[] bytes, string ext = ".png", string mime = "image/png", long? declared = null) =>
        new ReviewImageInspector().InspectAsync(new(new MemoryStream(bytes), "review" + ext, mime, declared), default);

    [Theory]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    [InlineData(".webp", "image/webp")]
    [InlineData(".JPG", " IMAGE/JPEG ")]
    public async Task ValidImages_AreCompletelyDecoded(string ext, string mime)
    {
        var result = await Inspect(Image(ext), ext, mime);
        result.IsAccepted.Should().BeTrue();
        result.Rejection.Should().BeNull();
        result.Image!.Width.Should().Be(3);
        result.Image.Height.Should().Be(2);
        using var data = SKData.CreateCopy(result.Image.Bytes);
        using var codec = SKCodec.Create(data);
        codec.Should().NotBeNull();
        codec!.FrameCount.Should().BeLessThanOrEqualTo(1);
    }

    [Theory]
    [InlineData(".jpg", ".png", "image/jpeg")]
    [InlineData(".jpg", ".jpg", "image/png")]
    [InlineData(".png", ".jpg", "image/png")]
    [InlineData(".webp", ".png", "image/png")]
    public async Task MetadataMustAgreeWithContent(string actual, string ext, string mime) =>
        (await Inspect(Image(actual), ext, mime)).Rejection.Should().Be(ReviewImageRejection.MetadataMismatch);

    [Theory]
    [InlineData("https://res.cloudinary.com/cloud/image/upload/a.png")]
    [InlineData("folder/public-id.png")]
    [InlineData("public-id")]
    public async Task ProviderIdentifiersAreNotLocalFiles(string name)
    {
        using var input = new MemoryStream(Image(".png"));
        var result = await new ReviewImageInspector().InspectAsync(new(input, name, "image/png"), default);
        result.Rejection.Should().Be(ReviewImageRejection.MetadataMismatch);
    }

    [Fact]
    public async Task EmptyIsRejected() => (await Inspect([])).Rejection.Should().Be(ReviewImageRejection.EmptyFile);

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    [InlineData(null)]
    public async Task NonSeekableShortReads_IgnoreDeclaredLength(long? declared)
    {
        using var input = new Unseekable(Image(".png"));
        var result = await new ReviewImageInspector().InspectAsync(new(input, "image.png", "image/png", declared), default);
        result.IsAccepted.Should().BeTrue();
        input.CanRead.Should().BeTrue("the caller owns its input stream");
    }

    [Theory]
    [InlineData(5_000_000, true)]
    [InlineData(5_000_001, false)]
    public async Task ActualEncodedBoundary_WithValidJpegSegments(int length, bool accepted)
    {
        var bytes = SizedJpeg(length);
        bytes.Length.Should().Be(length);
        var result = await Inspect(bytes, ".jpg", "image/jpeg", 1);
        result.IsAccepted.Should().Be(accepted);
        if (!accepted) result.Rejection.Should().Be(ReviewImageRejection.FileTooLarge);
    }

    [Fact]
    public async Task EndlessStream_IsReadOnlyToLimitPlusOne()
    {
        using var input = new Endless();
        var result = await new ReviewImageInspector().InspectAsync(new(input, "i.png", "image/png", 1), default);
        result.Rejection.Should().Be(ReviewImageRejection.FileTooLarge);
        input.Total.Should().Be(5_000_001);
    }

    [Theory]
    [InlineData(".gif", "image/gif", "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==")]
    [InlineData(".svg", "image/svg+xml", "PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciLz4=")]
    public async Task UnsupportedFormats(string ext, string mime, string base64) =>
        (await Inspect(Convert.FromBase64String(base64), ext, mime)).Rejection.Should().Be(ReviewImageRejection.UnsupportedFormat);

    [Theory]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    [InlineData(".webp", "image/webp")]
    public async Task CorruptTruncatedAndTrailingPayloadAreRejected(string ext, string mime)
    {
        var bytes = Image(ext);
        (await Inspect(bytes[..(bytes.Length / 2)], ext, mime)).IsAccepted.Should().BeFalse();
        (await Inspect([.. bytes, .. "<script>payload</script>"u8.ToArray()], ext, mime)).IsAccepted.Should().BeFalse();
        Array.Fill(bytes, (byte)0, 12, bytes.Length - 24);
        (await Inspect(bytes, ext, mime)).IsAccepted.Should().BeFalse();
    }

    [Theory]
    [InlineData(6000, 4000, true)]
    [InlineData(6001, 4000, false)]
    public async Task ApprovedPixelBoundary(int width, int height, bool accepted)
    {
        var result = await Inspect(Image(".png", width, height));
        result.IsAccepted.Should().Be(accepted);
        if (!accepted) result.Rejection.Should().Be(ReviewImageRejection.ResourceLimit);
    }

    [Theory]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    public async Task TrailingPayloadWithRepeatedTerminator_IsRejected(string ext, string mime)
    {
        var bytes = Image(ext);
        var terminator = ext == ".jpg" ? bytes[^2..] : bytes[^12..];
        (await Inspect([.. bytes, .. "<script>payload</script>"u8.ToArray(), .. terminator], ext, mime))
            .Rejection.Should().Be(ReviewImageRejection.InvalidImage);
    }

    [Fact]
    public async Task ZeroDimension_IsRejected()
    {
        var bytes = Image(".png");
        Array.Clear(bytes, 16, 4);
        (await Inspect(bytes)).IsAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task AnimatedWebp_FixtureHasTwoFrames_AndIsRejected()
    {
        var bytes = AnimatedWebp();
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        codec.Should().NotBeNull();
        codec!.FrameCount.Should().Be(2, "prove animation fixture, not merely random corrupt bytes");
        (await Inspect(bytes, ".webp", "image/webp")).Rejection.Should().Be(ReviewImageRejection.AnimatedImage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_PropagatesBeforeAndDuringRead(bool during)
    {
        using var cts = new CancellationTokenSource();
        using var input = new Unseekable(Image(".png"), during ? cts : null);
        if (!during) cts.Cancel();
        Func<Task> action = () => new ReviewImageInspector().InspectAsync(new(input, "i.png", "image/png"), cts.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private static byte[] Image(string ext, int width = 3, int height = 2)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(ext.ToLowerInvariant() switch { ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg, ".webp" => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Png }, 90);
        return data.ToArray();
    }

    private static byte[] SizedJpeg(int length)
    {
        var jpeg = Image(".jpg");
        using var output = new MemoryStream();
        output.Write(jpeg.AsSpan(0, 2));
        int left = length - jpeg.Length;
        while (left > 0)
        {
            int size = Math.Min(left, 65537);
            if (left - size is > 0 and < 4) size -= 4;
            output.Write([0xff, 0xe2, (byte)((size - 2) >> 8), (byte)(size - 2)]);
            output.Write(new byte[size - 4]);
            left -= size;
        }
        output.Write(jpeg.AsSpan(2));
        return output.ToArray();
    }

    private static byte[] Chunk(string name, byte[] payload)
    {
        byte[] result = new byte[8 + payload.Length + (payload.Length & 1)];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] AnimatedWebp()
    {
        var single = Image(".webp");
        var chunks = single[12..];
        byte[] header = new byte[10]; header[0] = 2; header[4] = 2; header[7] = 1;
        byte[] frame = new byte[16]; frame[6] = 2; frame[9] = 1; frame[12] = 100; frame[15] = 2;
        byte[] body = [.. "WEBP"u8.ToArray(), .. Chunk("VP8X", header), .. Chunk("ANIM", new byte[6]),
            .. Chunk("ANMF", [.. frame, .. chunks]), .. Chunk("ANMF", [.. frame, .. chunks])];
        byte[] result = [.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. body];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)body.Length);
        return result;
    }

    private sealed class Unseekable(byte[] bytes, CancellationTokenSource? cancel = null) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            cancel?.Cancel();
            token.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], token);
        }
    }

    private sealed class Endless : Stream
    {
        public long Total { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); buffer.Span.Clear(); Total += buffer.Length; return ValueTask.FromResult(buffer.Length); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}