using Microsoft.Extensions.Options;

using SkiaSharp;

using TripMate.Application.Features.TripReviews.Media;
using TripMate.Infrastructure.Media.Cloudinary;

namespace TripMate.Infrastructure.UnitTests.Media;

internal sealed class CloudinarySmokeFactAttribute : FactAttribute
{
    public CloudinarySmokeFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TRIPMATE_CLOUDINARY_SMOKE"), "1", StringComparison.Ordinal))
        {
            Skip = "Set TRIPMATE_CLOUDINARY_SMOKE=1 to opt in to the real development-provider smoke.";
            return;
        }
        string[] required = ["Cloudinary__CloudName", "Cloudinary__ApiKey", "Cloudinary__ApiSecret"];
        if (required.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Development Cloudinary credentials are required for the opt-in real-provider smoke.";
    }
}

public sealed class CloudinaryReviewMediaSmokeTests
{
    [CloudinarySmokeFact]
    [Trait("Category", "CloudinarySmoke")]
    public async Task RealProvider_UploadProbeDeleteAndConfirmAbsent()
    {
        var root = Environment.GetEnvironmentVariable("Cloudinary__ReviewMediaFolderRoot");
        root = string.IsNullOrWhiteSpace(root) ? "tripmate/reviews" : root.Trim('/');
        var options = Options.Create(new ReviewCloudinaryOptions
        {
            CloudName = Environment.GetEnvironmentVariable("Cloudinary__CloudName")!,
            ApiKey = Environment.GetEnvironmentVariable("Cloudinary__ApiKey")!,
            ApiSecret = Environment.GetEnvironmentVariable("Cloudinary__ApiSecret")!,
            ReviewMediaFolderRoot = root + "/smoke"
        });
        var storage = new CloudinaryReviewMediaStorage(new ReviewCloudinarySdkClient(options), options);
        var opaqueId = Guid.NewGuid().ToString("N");
        var image = new InspectedReviewImage(SyntheticPng(), "image/png", ".png", 2, 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        try
        {
            var uploaded = await storage.UploadAsync(opaqueId, image, timeout.Token);
            Assert.Equal(ReviewMediaStorageOutcome.Success, uploaded.Outcome);
            Assert.NotNull(uploaded.DeliveryUrl);
            Assert.StartsWith("https://", uploaded.DeliveryUrl, StringComparison.Ordinal);

            var visible = await storage.ProbeAsync(opaqueId, timeout.Token);
            Assert.Equal(ReviewMediaStorageOutcome.Success, visible.Outcome);
            Assert.StartsWith("https://", visible.DeliveryUrl, StringComparison.Ordinal);

            var deleted = await storage.DestroyAsync(opaqueId, timeout.Token);
            Assert.Contains(deleted.Outcome, new[] { ReviewMediaStorageOutcome.Success, ReviewMediaStorageOutcome.AlreadyAbsent });
            Assert.True(await ConfirmAbsent(storage, opaqueId, timeout.Token));
        }
        finally
        {
            // The final assertion is deliberately repeated after best-effort exact cleanup:
            // a green smoke must prove that it left no operation-owned asset behind.
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await storage.DestroyAsync(opaqueId, cleanupTimeout.Token);
            Assert.True(await ConfirmAbsent(storage, opaqueId, cleanupTimeout.Token));
        }
    }

    private static byte[] SyntheticPng()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(new SKColor(18, 151, 137, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static async Task<bool> ConfirmAbsent(IReviewMediaStorage storage, string id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if ((await storage.ProbeAsync(id, ct)).Outcome == ReviewMediaStorageOutcome.AlreadyAbsent)
                return true;
            if (attempt < 9)
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return false;
    }
}