using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Application.Features.Bookings.DirectRefunds.Evidence;
using TripMate.Application.Features.Bookings.DirectRefunds.Get;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.DirectRefunds;

public sealed class DirectRefundDetailEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainsTransferWhenReopenedOrCompleted(bool completed)
    {
        await using var db = CreateDb();
        var booking = Booking.Create(10, 20, 1, 10000, BookingStatus.Confirmed, PaymentStatus.Paid);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var refund = DirectRefundCase.Create(booking.Id, DirectRefundCase.SourcePolicyCancellation, 10000, 10000, Now);
        refund.ConfirmRecipient("VCB", "1234", "Traveler", Now);
        db.Set<DirectRefundCase>().Add(refund);
        await db.SaveChangesAsync();
        refund.MarkTransferReported(Now);
        refund.Dispute(Now);
        if (completed) refund.ResolveDisputeCompleted("Verified", Now);
        else refund.ResolveDisputeReopen("MoreEvidenceRequired", "More proof", Now);
        db.Set<DirectRefundTransfer>().Add(DirectRefundTransfer.Create(refund.Id, 10000, "FT-original", Now, Now, "VCB", "1234", "Traveler"));
        await db.SaveChangesAsync();
        var result = await new GetDirectRefundCaseQueryHandler(db, Admin()).Handle(new(refund.Id), default);
        Assert.True(result.IsSuccess);
        Assert.Equal("FT-original", result.Value!.PendingTransfer?.BankReference);
    }

    [Fact]
    public async Task AdminCanReadOnlyEvidenceBelongingToRequestedCase()
    {
        await using var db = CreateDb();
        var evidence = DirectRefundEvidence.Create(1, "proof.png", "image/png", 3, "private-key", "checksum", Now);
        db.Set<DirectRefundEvidence>().Add(evidence);
        await db.SaveChangesAsync();
        var storage = new Storage();
        var handler = new DownloadDirectRefundEvidenceQueryHandler(db, Admin(), storage);
        var wrongCase = await handler.Handle(new(2, evidence.Id), default);
        Assert.Equal(DirectRefundErrorCodes.NotFound, wrongCase.ErrorCode);
        Assert.Equal(0, storage.Reads);
        var valid = await handler.Handle(new(1, evidence.Id), default);
        Assert.True(valid.IsSuccess);
        Assert.Equal(new byte[] { 1, 2, 3 }, valid.Value!.Bytes);
        Assert.Equal("proof.png", valid.Value.FileName);
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("TourOperator")]
    [InlineData("Traveler")]
    public async Task NonAdminCannotReadEvidence(string role)
    {
        await using var db = CreateDb();
        var storage = new Storage();
        var result = await new DownloadDirectRefundEvidenceQueryHandler(db,
            new FakeCurrentUserService { UserId = 1, Role = role }, storage).Handle(new(1, 1), default);
        Assert.Equal(DirectRefundErrorCodes.AccessDenied, result.ErrorCode);
        Assert.Equal(0, storage.Reads);
    }

    [Fact]
    public async Task MissingStorageFileReturnsExpectedFailure()
    {
        await using var db = CreateDb();
        var evidence = DirectRefundEvidence.Create(1, "proof.png", "image/png", 3, "missing", "checksum", Now);
        db.Set<DirectRefundEvidence>().Add(evidence);
        await db.SaveChangesAsync();
        var result = await new DownloadDirectRefundEvidenceQueryHandler(db, Admin(), new Storage { Missing = true })
            .Handle(new(1, evidence.Id), default);
        Assert.False(result.IsSuccess);
    }

    private static FakeCurrentUserService Admin() => new() { UserId = 1, Role = nameof(UserRole.Administrator) };

    [Fact]
    public async Task CorruptedStorageLengthIsRejected()
    {
        await using var db = CreateDb();
        var evidence = DirectRefundEvidence.Create(1, "proof.png", "image/png", 10, "bad-size", "checksum", Now);
        db.Set<DirectRefundEvidence>().Add(evidence);
        await db.SaveChangesAsync();
        var result = await new DownloadDirectRefundEvidenceQueryHandler(db, Admin(), new Storage())
            .Handle(new(1, evidence.Id), default);
        Assert.False(result.IsSuccess);
    }
    private static RefundDb CreateDb() => new(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class RefundDb(DbContextOptions<TestDbContext> options) : TestDbContext(options), IApplicationDbContext
    {
        DbSet<DirectRefundCase> IApplicationDbContext.DirectRefundCases => Set<DirectRefundCase>();
        DbSet<DirectRefundTransfer> IApplicationDbContext.DirectRefundTransfers => Set<DirectRefundTransfer>();
        DbSet<DirectRefundEvidence> IApplicationDbContext.DirectRefundEvidences => Set<DirectRefundEvidence>();
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<DirectRefundCase>();
            builder.Entity<DirectRefundTransfer>();
            builder.Entity<DirectRefundEvidence>();
        }
    }

    private sealed class Storage : IDirectRefundEvidenceStorage
    {
        public int Reads { get; private set; }
        public bool Missing { get; init; }
        public Task<byte[]?> ReadAsync(string key, CancellationToken token) { Reads++; return Task.FromResult<byte[]?>(Missing ? null : [1, 2, 3]); }
        public Task<StoredDirectRefundEvidence?> StoreAsync(DirectRefundEvidenceUpload upload, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken token) => throw new NotSupportedException();
        public Uri? CreateTemporaryDownloadUrl(string key, string contentType, DateTimeOffset expires) => throw new NotSupportedException();
    }
}