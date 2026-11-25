using FluentAssertions;

using SkiaSharp;

using TripMate.Infrastructure.Contracts;

namespace TripMate.Infrastructure.UnitTests.Contracts;

public sealed class ContractSignatureImageInspectorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlankOrTransparentImage_IsNotASignature(bool white)
    {
        var result = new ContractSignatureImageInspector().Inspect(Image(white, false));
        result.ErrorCode.Should().Be("contract.signature_empty");
    }

    [Fact]
    public void RealStroke_ProducesPrivateCanonicalPngAndContentHashes()
    {
        var input = Image(false, true);
        var result = new ContractSignatureImageInspector().Inspect(input);
        result.IsSuccess.Should().BeTrue();
        result.Value.Width.Should().Be(600);
        result.Value.Height.Should().Be(200);
        result.Value.SourceSha256.Should().HaveLength(64);
        result.Value.CanonicalSha256.Should().HaveLength(64);
        using var decoded = SKBitmap.Decode(result.Value.PngBytes);
        decoded.Should().NotBeNull();
    }

    [Fact]
    public void CorruptOrOversizedImage_IsRejectedBeforeDecoding()
    {
        var inspector = new ContractSignatureImageInspector();
        inspector.Inspect([1, 2, 3]).ErrorCode.Should().Be("contract.signature_invalid");
        inspector.Inspect(new byte[512 * 1024 + 1]).ErrorCode.Should().Be("contract.signature_too_large");
    }

    [Fact]
    public void ExcessiveDimensions_AreRejected()
    {
        using var bitmap = new SKBitmap(2049, 32);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        new ContractSignatureImageInspector().Inspect(data.ToArray()).ErrorCode.Should().Be("contract.signature_invalid");
    }

    private static byte[] Image(bool white, bool stroke)
    {
        using var bitmap = new SKBitmap(600, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(white ? SKColors.White : SKColors.Transparent);
        if (stroke)
        {
            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 4, IsAntialias = true };
            canvas.DrawLine(30, 130, 400, 60, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}