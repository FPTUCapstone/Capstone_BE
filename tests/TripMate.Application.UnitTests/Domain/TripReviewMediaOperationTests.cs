using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TripReviewMediaOperationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
    private static TripReviewMediaOperation Create(long bytes = 5000000, int width = 6000, int height = 4000, string mime = "image/png", string ext = ".png", string id = "owned-asset") =>
        TripReviewMediaOperation.Reserve(Guid.NewGuid(), Guid.NewGuid(), 1, 1, 0, id, mime, ext, bytes, width, height, Now);

    [Fact]
    public void ReservedUploadedAdopted_PreservesIdentityAndMetadata()
    {
        var op = Create(); var id = op.Id; var batch = op.BatchId;
        op.State.Should().Be("Reserved"); op.DeliveryUrl.Should().BeNull();
        op.TryRecordUpload("https://images.example.invalid/a", 6000000, Now.AddMinutes(1)).Should().BeTrue();
        op.StoredByteLength.Should().Be(6000000); op.InputByteLength.Should().Be(5000000);
        op.TryAdopt(Now.AddMinutes(2)).Should().BeTrue();
        op.State.Should().Be("Adopted"); op.Id.Should().Be(id); op.BatchId.Should().Be(batch);
        op.TryMarkCleanupPending(Now.AddMinutes(3)).Should().BeFalse();
        op.TryRecordUpload("https://images.example.invalid/b", 1, Now.AddMinutes(3)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupPending_IsTerminalForAdoptionAndPreservesTuple(bool uploaded)
    {
        var op = Create();
        if (uploaded) op.TryRecordUpload("https://images.example.invalid/a", 1, Now);
        op.TryMarkCleanupPending(Now).Should().BeTrue();
        op.State.Should().Be("CleanupPending");
        op.TryAdopt(Now).Should().BeFalse();
        op.TryRecordUpload("https://images.example.invalid/a", 1, Now).Should().BeFalse();
        op.UploadedAtUtc.HasValue.Should().Be(uploaded);
        op.StoredByteLength.HasValue.Should().Be(uploaded);
        op.AdoptedAtUtc.Should().BeNull(); op.CleanedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(5000001, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 6001, 4000)]
    public void InvalidInputMetadataIsRejected(long bytes, int width, int height)
    { Action act = () => Create(bytes, width, height); act.Should().Throw<ArgumentException>(); }

    [Theory]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/jpeg", ".png")]
    public void InvalidFormatIsRejected(string mime, string ext)
    { Action act = () => Create(mime: mime, ext: ext); act.Should().Throw<ArgumentException>(); }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void EmptyIdentifierIsRejected(string id)
    { Action act = () => Create(id: id); act.Should().Throw<ArgumentException>(); }

    [Theory]
    [InlineData("http://images.example.invalid/a", 1)]
    [InlineData("not-url", 1)]
    [InlineData("https://images.example.invalid/a", 0)]
    public void InvalidUploadTupleDoesNotMutate(string url, long size)
    {
        var op = Create(); op.TryRecordUpload(url, size, Now).Should().BeFalse();
        op.State.Should().Be("Reserved"); op.DeliveryUrl.Should().BeNull();
    }

    [Fact]
    public void ReservedCannotBeAdoptedAndOwnershipHasNoPublicSetter()
    {
        var op = Create(); op.TryAdopt(Now).Should().BeFalse();
        foreach (var property in typeof(TripReviewMediaOperation).GetProperties())
            property.SetMethod?.IsPublic.Should().BeFalse();
    }

    [Fact]
    public void ServiceReservation_PreservesNamespaceSafeParent()
    {
        var operation = TripReviewMediaOperation.ReserveForService(
            Guid.NewGuid(), Guid.NewGuid(), 7, 3, 0, "service-asset", "image/png", ".png",
            1, 1, 1, Now);

        operation.BookingId.Should().BeNull();
        operation.ServiceBookingId.Should().Be(7);
        operation.TravelerUserId.Should().Be(3);
    }
}