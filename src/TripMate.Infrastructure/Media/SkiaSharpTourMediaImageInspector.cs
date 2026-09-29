using System.Buffers.Binary;
using System.Security.Cryptography;

using SkiaSharp;

using TripMate.Application.Common.Media;

namespace TripMate.Infrastructure.Media;

public sealed class SkiaSharpTourMediaImageInspector : ITourMediaImageInspector
{
    private const string CorruptErrorCode = "TOUR_MEDIA_IMAGE_CORRUPT";
    private const string EmptyErrorCode = "TOUR_MEDIA_IMAGE_EMPTY";
    private const string FileTooLargeErrorCode = "TOUR_MEDIA_IMAGE_TOO_LARGE";
    private const string UnsupportedErrorCode = "TOUR_MEDIA_IMAGE_UNSUPPORTED";
    private const string MismatchErrorCode = "TOUR_MEDIA_IMAGE_METADATA_MISMATCH";
    private const string InvalidDimensionsErrorCode = "TOUR_MEDIA_IMAGE_INVALID_DIMENSIONS";
    private const string PixelAreaErrorCode = "TOUR_MEDIA_IMAGE_PIXEL_LIMIT";

    private readonly long maxEncodedBytes;
    private readonly long maxPixelArea;

    public SkiaSharpTourMediaImageInspector()
        : this(TourMediaImagePolicy.MaxEncodedBytes, TourMediaImagePolicy.MaxPixelArea)
    {
    }

    internal SkiaSharpTourMediaImageInspector(long maxEncodedBytes, long maxPixelArea)
    {
        if (maxEncodedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEncodedBytes));
        }

        if (maxPixelArea <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPixelArea));
        }

        this.maxEncodedBytes = maxEncodedBytes;
        this.maxPixelArea = maxPixelArea;
    }

    public async Task<TourMediaImageInspectionResult> InspectAsync(
        TourMediaImageSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Content);
        cancellationToken.ThrowIfCancellationRequested();

        if (source.Length <= 0)
        {
            return Reject(TourMediaImageRejectionReason.EmptyFile, EmptyErrorCode);
        }

        if (source.Length > maxEncodedBytes)
        {
            return Reject(TourMediaImageRejectionReason.FileTooLarge, FileTooLargeErrorCode);
        }

        byte[]? encodedBytes = await ReadBoundedAsync(source.Content, source.Length, cancellationToken);
        if (encodedBytes is null)
        {
            return Reject(TourMediaImageRejectionReason.FileTooLarge, FileTooLargeErrorCode);
        }

        if (encodedBytes.Length == 0)
        {
            return Reject(TourMediaImageRejectionReason.EmptyFile, EmptyErrorCode);
        }

        SupportedImageFormat? format = DetectFormat(encodedBytes);
        if (format is null)
        {
            return LooksLikeKnownUnsupportedImage(encodedBytes)
                ? Reject(TourMediaImageRejectionReason.UnsupportedFormat, UnsupportedErrorCode)
                : Reject(TourMediaImageRejectionReason.CorruptImage, CorruptErrorCode);
        }

        if (!HasExactContainerBoundary(format.Value, encodedBytes))
        {
            return Reject(TourMediaImageRejectionReason.CorruptImage, CorruptErrorCode);
        }

        if (!MatchesDeclaredMetadata(format.Value, source.DeclaredContentType, source.FileName))
        {
            return Reject(TourMediaImageRejectionReason.MetadataMismatch, MismatchErrorCode);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var data = SKData.CreateCopy(encodedBytes);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != format.Value.EncodedFormat)
        {
            return Reject(TourMediaImageRejectionReason.CorruptImage, CorruptErrorCode);
        }

        int width = codec.Info.Width;
        int height = codec.Info.Height;
        if (width <= 0 || height <= 0)
        {
            return Reject(TourMediaImageRejectionReason.InvalidDimensions, InvalidDimensionsErrorCode);
        }

        long pixelArea = checked((long)width * height);
        if (pixelArea > maxPixelArea)
        {
            return Reject(TourMediaImageRejectionReason.PixelAreaTooLarge, PixelAreaErrorCode);
        }

        var imageInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(imageInfo);
        SKCodecResult decodeResult = codec.GetPixels(imageInfo, bitmap.GetPixels());
        if (decodeResult != SKCodecResult.Success)
        {
            return Reject(TourMediaImageRejectionReason.CorruptImage, CorruptErrorCode);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var image = SKImage.FromBitmap(bitmap);
        using SKData? sanitized = image.Encode(format.Value.EncodedFormat, format.Value.Quality);
        if (sanitized is null)
        {
            return Reject(TourMediaImageRejectionReason.CorruptImage, CorruptErrorCode);
        }

        byte[] sanitizedBytes = sanitized.ToArray();
        string fingerprint = Convert.ToHexString(SHA256.HashData(sanitizedBytes));
        return TourMediaImageInspectionResult.Accepted(
            new InspectedTourMediaImage(
                sanitizedBytes,
                format.Value.ContentType,
                format.Value.Extension,
                width,
                height,
                fingerprint));
    }

    private async Task<byte[]?> ReadBoundedAsync(
        Stream content,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        var capacity = (int)Math.Min(declaredLength, maxEncodedBytes);
        await using var buffer = new MemoryStream(capacity);
        byte[] chunk = new byte[81_920];

        while (true)
        {
            int read = await content.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxEncodedBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static SupportedImageFormat? DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return new SupportedImageFormat("image/jpeg", ".jpg", SKEncodedImageFormat.Jpeg, 90);
        }

        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return new SupportedImageFormat("image/png", ".png", SKEncodedImageFormat.Png, 100);
        }

        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return new SupportedImageFormat("image/webp", ".webp", SKEncodedImageFormat.Webp, 90);
        }

        return null;
    }

    private static bool LooksLikeKnownUnsupportedImage(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 6 &&
        (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8));

    private static bool HasExactContainerBoundary(
        SupportedImageFormat format,
        ReadOnlySpan<byte> bytes) =>
        format.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg =>
                bytes.Length >= 2 && bytes[^2] == 0xFF && bytes[^1] == 0xD9,
            SKEncodedImageFormat.Png =>
                bytes.Length >= 12 &&
                bytes.Slice(bytes.Length - 12, 4).SequenceEqual(new byte[4]) &&
                bytes.Slice(bytes.Length - 8, 4).SequenceEqual("IEND"u8),
            SKEncodedImageFormat.Webp =>
                bytes.Length >= 12 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) + 8L == bytes.Length,
            _ => false,
        };

    private static bool MatchesDeclaredMetadata(
        SupportedImageFormat format,
        string declaredContentType,
        string fileName)
    {
        if (!string.Equals(
                declaredContentType?.Trim(),
                format.ContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string extension = Path.GetExtension(fileName ?? string.Empty);
        return format.ContentType == "image/jpeg"
            ? extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            : extension.Equals(format.Extension, StringComparison.OrdinalIgnoreCase);
    }

    private static TourMediaImageInspectionResult Reject(
        TourMediaImageRejectionReason reason,
        string safeErrorCode) =>
        TourMediaImageInspectionResult.Rejected(reason, safeErrorCode);

    private readonly record struct SupportedImageFormat(
        string ContentType,
        string Extension,
        SKEncodedImageFormat EncodedFormat,
        int Quality);
}