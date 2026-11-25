using System.Security.Cryptography;

using SkiaSharp;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Contracts.Common;

namespace TripMate.Infrastructure.Contracts;

/// <summary>Validates capture pixels, not the identity or authenticity of the handwriting.</summary>
public sealed class ContractSignatureImageInspector : IContractSignatureImageInspector
{
    public const int MaximumEncodedBytes = 512 * 1024;

    public Result<InspectedContractSignature> Inspect(byte[] bytes)
    {
        if (bytes.Length > MaximumEncodedBytes) return Failure("contract.signature_too_large");
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return Failure("contract.signature_invalid");
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.FrameCount > 1)
            return Failure("contract.signature_invalid");
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width is < 64 or > 2048 || height is < 32 or > 1024 || (long)width * height > 2_097_152)
            return Failure("contract.signature_invalid");
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
            return Failure("contract.signature_invalid");
        var visiblePixels = 0;
        for (var y = 0; y < height && visiblePixels < 32; y++)
            for (var x = 0; x < width && visiblePixels < 32; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Alpha >= 32 && (color.Red < 240 || color.Green < 240 || color.Blue < 240)) visiblePixels++;
            }
        if (visiblePixels < 32) return Failure("contract.signature_empty");
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var canonical = encoded.ToArray();
        if (canonical.Length > MaximumEncodedBytes) return Failure("contract.signature_too_large");
        return Result.Success(new InspectedContractSignature(canonical, Hash(bytes), Hash(canonical), width, height));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static Result<InspectedContractSignature> Failure(string code) =>
        Result.Failure<InspectedContractSignature>(code, "Draw a visible signature and try again with a valid PNG capture.");
}