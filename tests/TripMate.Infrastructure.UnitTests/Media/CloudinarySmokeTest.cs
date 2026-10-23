using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Media;
using TripMate.Infrastructure.Media;
using TripMate.Infrastructure.Media.Cloudinary;

using Xunit.Abstractions;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class CloudinarySmokeTest(ITestOutputHelper output)
{
    [CloudinarySmokeFact]
    public async Task UploadsHttpsImageAndConfirmsProviderDeletion()
    {
        var options = new CloudinaryOptions
        {
            CloudName = RequiredEnvironmentVariable("Cloudinary__CloudName"),
            ApiKey = RequiredEnvironmentVariable("Cloudinary__ApiKey"),
            ApiSecret = RequiredEnvironmentVariable("Cloudinary__ApiSecret"),
            TourMediaFolderRoot = RequiredEnvironmentVariable("Cloudinary__TourMediaFolderRoot"),
        };
        CloudinaryOptionsValidator validator = new();
        validator.Validate(CloudinaryOptions.SectionName, options).Succeeded.Should().BeTrue();

        var cloudinaryOptions = Options.Create(options);
        var storage = new CloudinaryTourMediaStorage(
            new CloudinarySdkClient(cloudinaryOptions),
            cloudinaryOptions,
            NullLogger<CloudinaryTourMediaStorage>.Instance);
        string publicId = $"{options.TourMediaFolderRoot}/tm207-smoke/{Guid.NewGuid():N}";
        string publicIdHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(publicId)))[..12].ToLowerInvariant();
        byte[] generatedPng = CreateOnePixelPng();
        await using var generatedImage = new MemoryStream(generatedPng, writable: false);
        TourMediaImageInspectionResult inspection = await new SkiaSharpTourMediaImageInspector().InspectAsync(
            new TourMediaImageSource(generatedImage, generatedPng.Length, "smoke.png", "image/png"),
            CancellationToken.None);
        InspectedTourMediaImage image = inspection.Image ??
            throw new InvalidOperationException("The generated harmless smoke image failed local validation.");
        var uploaded = false;
        var cleanupConfirmed = false;

        output.WriteLine("Cloudinary smoke identity: cloud={0}; folder={1}/tm207-smoke; publicIdSha256Prefix={2}",
            options.CloudName,
            options.TourMediaFolderRoot,
            publicIdHash);

        try
        {
            // Treat an ambiguous upload response as possibly committed and always attempt cleanup.
            uploaded = true;
            TourMediaStorageUploadResult upload = await storage.UploadAsync(
                new TourMediaStorageUpload(publicId, image.ContentType, image.Bytes),
                CancellationToken.None);
            if (upload.FailureKind is not null)
            {
                output.WriteLine("Upload failed with safe provider code {0}.", upload.SafeErrorCode ?? "unclassified");
            }

            upload.FailureKind.Should().BeNull("the test image must be accepted by the configured Cloudinary account");
            upload.DeliveryUrl.Should().NotBeNull();
            upload.DeliveryUrl!.Scheme.Should().Be(Uri.UriSchemeHttps);
            output.WriteLine("Upload verified: HTTPS delivery URL returned; URL omitted from test output.");

            TourMediaStorageDeleteResult deletion = await storage.DestroyAsync(publicId, CancellationToken.None);
            deletion.Outcome.Should().Be(TourMediaStorageDeleteOutcome.Deleted);
            cleanupConfirmed = true;
            output.WriteLine("Cleanup verified: generated smoke asset destroyed.");
        }
        finally
        {
            if (uploaded && !cleanupConfirmed)
            {
                TourMediaStorageDeleteResult cleanup = await storage.DestroyAsync(publicId, CancellationToken.None);
                if (cleanup.Outcome is not (TourMediaStorageDeleteOutcome.Deleted or
                    TourMediaStorageDeleteOutcome.AlreadyAbsent))
                {
                    throw new InvalidOperationException(
                        "Cloudinary smoke asset cleanup could not be confirmed; see the safe provider outcome only.");
                }

                output.WriteLine("Cleanup fallback verified after an earlier test failure.");
            }
        }
    }

    [CloudinarySmokeFact]
    public async Task OperatorDocumentsRequireSignedDownloadAndAreDeleted()
    {
        var options = new CloudinaryOptions
        {
            CloudName = RequiredEnvironmentVariable("Cloudinary__CloudName"),
            ApiKey = RequiredEnvironmentVariable("Cloudinary__ApiKey"),
            ApiSecret = RequiredEnvironmentVariable("Cloudinary__ApiSecret"),
            TourMediaFolderRoot = RequiredEnvironmentVariable("Cloudinary__TourMediaFolderRoot"),
            OperatorDocumentsFolderRoot = RequiredEnvironmentVariable("Cloudinary__OperatorDocumentsFolderRoot"),
        };
        new CloudinaryOptionsValidator().Validate(CloudinaryOptions.SectionName, options)
            .Succeeded.Should().BeTrue();

        var storage = new CloudinaryOperatorDocumentStorage(
            new CloudinarySdkClient(Options.Create(options)), Options.Create(options));
        using var http = new HttpClient();

        foreach (bool isPdf in new[] { false, true })
        {
            string publicId = $"{options.OperatorDocumentsFolderRoot}/tm02-smoke/{Guid.NewGuid():N}" +
                (isPdf ? ".pdf" : string.Empty);
            string contentType = isPdf ? "application/pdf" : "image/png";
            byte[] bytes = isPdf
                ? Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF")
                : CreateOnePixelPng();
            bool uploadAttempted = false;

            try
            {
                uploadAttempted = true;
                OperatorDocumentStorageUploadResult upload = await storage.UploadAsync(
                    new OperatorDocumentStorageUpload(publicId, contentType, bytes),
                    CancellationToken.None);
                upload.FailureKind.Should().BeNull("the configured provider must accept private documents");
                upload.DeliveryUrl.Should().NotBeNull();
                upload.DeliveryUrl!.Scheme.Should().Be("cloudinary-operator");

                Uri? signedUrl = storage.CreateTemporaryDownloadUrl(
                    upload.DeliveryUrl.AbsoluteUri, DateTimeOffset.UtcNow.AddMinutes(5));
                signedUrl.Should().NotBeNull();
                using HttpResponseMessage signedResponse = await http.GetAsync(signedUrl);
                signedResponse.IsSuccessStatusCode.Should().BeTrue(
                    "an Administrator-issued short-lived URL must retrieve the asset");

                string resourceType = isPdf ? "raw" : "image";
                string publicPath = isPdf ? publicId : publicId + ".png";
                var unsignedUrl = new Uri(
                    $"https://res.cloudinary.com/{options.CloudName}/{resourceType}/upload/{publicPath}");
                using HttpResponseMessage unsignedResponse = await http.GetAsync(unsignedUrl);
                unsignedResponse.IsSuccessStatusCode.Should().BeFalse(
                    "an authenticated operator document must not be publicly delivered");
                output.WriteLine("Private {0} smoke verified; asset URL and content omitted.",
                    isPdf ? "PDF" : "PNG");
            }
            finally
            {
                if (uploadAttempted)
                {
                    OperatorDocumentStorageDeleteResult cleanup = await storage.DeleteAsync(
                        publicId, contentType, CancellationToken.None);
                    cleanup.Outcome.Should().BeOneOf(
                        OperatorDocumentStorageDeleteOutcome.Deleted,
                        OperatorDocumentStorageDeleteOutcome.AlreadyAbsent);
                }
            }
        }
    }

    private static string RequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required smoke-test setting {name} is missing.");

    private static byte[] CreateOnePixelPng()
    {
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 1);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(png, "IHDR", header);

        using var imageData = new MemoryStream();
        using (var compressor = new ZLibStream(imageData, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write([0, 0, 128, 255]);
        }

        WriteChunk(png, "IDAT", imageData.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream destination, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        destination.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        destination.Write(typeBytes);
        destination.Write(data);

        uint crc = 0xFFFFFFFF;
        foreach (byte value in typeBytes.AsSpan().ToArray().Concat(data.ToArray()))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320;
            }
        }

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        destination.Write(checksum);
    }

    private sealed class CloudinarySmokeFactAttribute : FactAttribute
    {
        public CloudinarySmokeFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("TRIPMATE_CLOUDINARY_SMOKE"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                Skip = "Set TRIPMATE_CLOUDINARY_SMOKE=true to run this opt-in development-provider test.";
            }
        }
    }
}