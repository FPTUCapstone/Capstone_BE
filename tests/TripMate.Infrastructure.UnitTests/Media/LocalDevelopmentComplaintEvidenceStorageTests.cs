using FluentAssertions;

using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Infrastructure.Media;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class LocalDevelopmentComplaintEvidenceStorageTests
{
    [Fact]
    public async Task StoreAsync_ThenDeleteAsync_StoresPrivateOpaqueKeyAndCleansItUp()
    {
        var storage = new LocalDevelopmentComplaintEvidenceStorage();
        var upload = new ComplaintEvidenceUpload("proof.png", "image/png", [137, 80, 78, 71]);

        StoredComplaintEvidence? stored = await storage.StoreAsync(upload, CancellationToken.None);

        stored.Should().NotBeNull();
        stored!.StorageObjectKey.Should().StartWith("local:");
        stored.ByteLength.Should().Be(upload.Bytes.Length);
        (await storage.DeleteAsync(stored.StorageObjectKey, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_RejectsNonLocalOrUnsafeKey()
    {
        var storage = new LocalDevelopmentComplaintEvidenceStorage();

        (await storage.DeleteAsync("image:tripmate/complaints/abc", CancellationToken.None)).Should().BeFalse();
        (await storage.DeleteAsync("local:../../escape", CancellationToken.None)).Should().BeFalse();
    }
}
