using System.Buffers.Binary;

using SkiaSharp;

using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Media;

namespace TripMate.Infrastructure.Reviews.Media;

public sealed class ReviewImageInspector : IReviewImageInspector
{
    // Matches TM-207 at develop e8013f9. No shared decoder contract exists on this branch.
    // RGBA8888 needs at most 96,000,000 pixel bytes; codec overhead is additional.
    private const long MaximumPixelArea = 24_000_000;

    public async Task<ReviewImageInspection> InspectAsync(ReviewImageSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Content);
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int count = (int)Math.Min(buffer.Length, TripReviewInputRules.MaximumPhotoBytes + 1 - output.Length);
            int read = await source.Content.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) break;
            output.Write(buffer, 0, read);
            if (output.Length > TripReviewInputRules.MaximumPhotoBytes) return Reject(ReviewImageRejection.FileTooLarge);
        }
        if (output.Length == 0) return Reject(ReviewImageRejection.EmptyFile);
        byte[] bytes = output.ToArray();
        var format = Detect(bytes);
        if (format is null) return Reject(ReviewImageRejection.UnsupportedFormat);
        string mime = format switch { SKEncodedImageFormat.Jpeg => "image/jpeg", SKEncodedImageFormat.Png => "image/png", _ => "image/webp" };
        string extension = format switch { SKEncodedImageFormat.Jpeg => ".jpg", SKEncodedImageFormat.Png => ".png", _ => ".webp" };
        string name = source.FileName ?? string.Empty;
        string actualExtension = Path.GetExtension(name);
        if (name.IndexOfAny(['/', '\\', ':']) >= 0 ||
            !(extension.Equals(actualExtension, StringComparison.OrdinalIgnoreCase) ||
              (format == SKEncodedImageFormat.Jpeg && actualExtension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))) ||
            !mime.Equals(source.ContentType?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Reject(ReviewImageRejection.MetadataMismatch);
        if (!HasCompleteContainer(bytes, format.Value)) return Reject(ReviewImageRejection.InvalidImage);

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != format) return Reject(ReviewImageRejection.InvalidImage);
        if (codec.FrameCount > 1 || (format == SKEncodedImageFormat.Webp && HasWebpAnimation(bytes)))
            return Reject(ReviewImageRejection.AnimatedImage);
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return Reject(ReviewImageRejection.InvalidImage);
        if ((long)info.Width * info.Height > MaximumPixelArea) return Reject(ReviewImageRejection.ResourceLimit);
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            return Reject(ReviewImageRejection.InvalidImage);
        cancellationToken.ThrowIfCancellationRequested();
        // Re-encode decoded pixels, not untrusted metadata. This is inspection, not storage.
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format.Value, 90);
        if (encoded is null) return Reject(ReviewImageRejection.InvalidImage);
        cancellationToken.ThrowIfCancellationRequested();
        return new(new(encoded.ToArray(), mime, extension, info.Width, info.Height), null);
    }

    private static ReviewImageInspection Reject(ReviewImageRejection reason) => new(null, reason);

    private static SKEncodedImageFormat? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return SKEncodedImageFormat.Jpeg;
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return SKEncodedImageFormat.Png;
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return SKEncodedImageFormat.Webp;
        return null;
    }

    private static bool HasCompleteContainer(ReadOnlySpan<byte> bytes, SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => HasExactJpegEnd(bytes),
        SKEncodedImageFormat.Png => HasExactPngEnd(bytes),
        SKEncodedImageFormat.Webp => bytes.Length >= 12 && (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]) + 8 == bytes.Length,
        _ => false
    };

    // Walk length-delimited segments and entropy markers: a second EOI must not
    // disguise payload after the first EOI. Pixel validity remains the codec's job.
    private static bool HasExactJpegEnd(ReadOnlySpan<byte> bytes)
    {
        int offset = 2;
        bool entropy = false;
        while (offset < bytes.Length)
        {
            if (entropy)
            {
                while (offset < bytes.Length && bytes[offset] != 255) offset++;
            }
            if (offset >= bytes.Length || bytes[offset++] != 255) return false;
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            if (offset >= bytes.Length) return false;
            byte marker = bytes[offset++];
            if (entropy && (marker == 0 || marker is >= 0xd0 and <= 0xd7)) continue;
            if (marker == 0xd9) return offset == bytes.Length;
            if (marker == 0 || marker == 0xd8 || marker is >= 0xd0 and <= 0xd7) return false;
            if (marker == 1) continue; // Standalone TEM marker.
            if (offset > bytes.Length - 2) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || length > bytes.Length - offset) return false;
            offset += length;
            entropy = marker == 0xda || (entropy && marker == 0xdc);
        }
        return false;
    }

    private static bool HasExactPngEnd(ReadOnlySpan<byte> bytes)
    {
        int offset = 8;
        while (offset <= bytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            long next = offset + 12L + length;
            if (next > bytes.Length) return false;
            if (bytes.Slice(offset + 4, 4).SequenceEqual("IEND"u8))
                return length == 0 && next == bytes.Length;
            offset = (int)next;
        }
        return false;
    }

    private static bool HasWebpAnimation(ReadOnlySpan<byte> bytes)
    {
        int offset = 12;
        while (offset <= bytes.Length - 8)
        {
            var type = bytes.Slice(offset, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8)) return true;
            if (type.SequenceEqual("VP8X"u8) && size > 0 && offset + 8 < bytes.Length && (bytes[offset + 8] & 2) != 0) return true;
            long next = offset + 8L + size + (size & 1);
            if (next > bytes.Length) break;
            offset = (int)next;
        }
        return false;
    }
}