using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Application.Features.Complaints.StaffReview;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.PostTripComplaints.Domain;

public sealed class StaffComplaintHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Version = [1, 2, 3, 4, 5, 6, 7, 8];
    private static FakeCurrentUserService User(string role = "Staff", long id = 4) => new() { UserId = id, Role = role };
    private static FakeDateTimeProvider Clock() => new() { UtcNow = Now };

    [Fact]
    public async Task DecisionIsStaffOnlyAndStaleVersionsDoNotCreateMoney()
    {
        await using var db = await Seed();
        var command = Command();
        foreach (var role in new[] { "Administrator", "TourOperator", "Traveler" })
            Assert.Equal(StaffComplaintAccess.Forbidden, (await Handler(db, User(role)).Handle(command, default)).ErrorCode);
        var stale = command with { ExpectedVersion = Convert.ToBase64String(new byte[8]) };
        Assert.Equal(StaffComplaintAccess.Conflict, (await Handler(db).Handle(stale, default)).ErrorCode);
        Assert.Empty(db.DirectRefundCases);
        Assert.Empty(db.ComplaintStaffReviewEvents);
    }

    [Fact]
    public async Task BindingPartialDecisionCreatesOneObligationAndReplayDoesNotDuplicateIt()
    {
        await using var db = await Seed();
        var command = Command();
        var result = await Handler(db).Handle(command, default);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("Approved", result.Value!.Status);
        var refund = Assert.Single(db.DirectRefundCases);
        Assert.Equal(3000, refund.RefundAmountVnd);
        Assert.Equal(1, refund.SourceRefId);
        Assert.Equal(DirectRefundCase.StatusAwaitingRecipient, refund.Status);
        Assert.Equal(3000, (await db.PostTripComplaints.SingleAsync()).ProposedRefundAmount);
        Assert.True((await Handler(db).Handle(command, default)).IsSuccess);
        Assert.Single(db.DirectRefundCases);
        Assert.Single(db.ComplaintStaffReviewEvents);
        Assert.Equal(StaffComplaintAccess.Conflict, (await Handler(db).Handle(command with { Amount = 4000 }, default)).ErrorCode);
        Assert.Single(db.DirectRefundCases);
    }

    [Fact]
    public async Task ExistingDisputedRefundStillReservesMoneyAndBlocksOverspending()
    {
        await using var db = await Seed();
        var reserved = DirectRefundCase.Create(1, DirectRefundCase.SourcePolicyCancellation, 8000, 10000, Now);
        Set(reserved, nameof(DirectRefundCase.Status), DirectRefundCase.StatusDisputed);
        db.DirectRefundCases.Add(reserved);
        await db.SaveChangesAsync();
        var result = await Handler(db).Handle(Command(), default);
        Assert.True(result.IsFailure);
        Assert.Equal(PostTripComplaintStatus.AwaitingStaffReview, (await db.PostTripComplaints.SingleAsync()).Status);
        Assert.Empty(db.ComplaintStaffReviewEvents);
        Assert.Single(db.DirectRefundCases);
    }

    [Fact]
    public async Task RejectionRequiresContractualBasisAndDoesNotCreateRefund()
    {
        await using var db = await Seed();
        var command = Command() with { Action = "Reject", Amount = null };
        Assert.True((await Handler(db).Handle(command with { ContractBasis = " " }, default)).IsFailure);
        Assert.True((await Handler(db).Handle(command, default)).IsSuccess);
        Assert.Equal(PostTripComplaintStatus.Rejected, (await db.PostTripComplaints.SingleAsync()).Status);
        Assert.Empty(db.DirectRefundCases);
    }

    [Fact]
    public async Task OtherTravelerCannotReadReviewOrDownloadEvidence()
    {
        await using var db = await Seed();
        var query = new GetStaffComplaintQueryHandler(db, User("Traveler", 99));
        Assert.Equal(StaffComplaintAccess.Forbidden, (await query.Handle(new(1, true), default)).ErrorCode);
        var own = await new GetStaffComplaintQueryHandler(db, User("Traveler", 2)).Handle(new(1, true), default);
        Assert.True(own.IsSuccess);
        Assert.Empty(own.Value!.AllowedActions);
    }

    [Fact]
    public async Task EvidenceRetryAfterCommitKeepsFilesAndReconsiderationNeverChangesRefund()
    {
        await using var db = await Seed();
        var decision = Command() with { Action = "RequestEvidence", Amount = null, TargetParty = "Traveler" };
        Assert.True((await Handler(db).Handle(decision, default)).IsSuccess);
        long requestId = (await db.ComplaintStaffReviewEvents.SingleAsync()).Id;
        var storage = new MemoryEvidence();
        db.ReplayNextTransaction = true;
        var party = new SubmitStaffComplaintEvidenceCommandHandler(db, User("Traveler", 2), Clock(), storage);
        var evidence = new SubmitStaffComplaintEvidenceCommand(1, Guid.NewGuid(), requestId, "Receipt attached", [new("receipt.pdf", "application/pdf", "%PDF-test"u8.ToArray())]);
        var submitted = await party.Handle(evidence, default);
        Assert.True(submitted.IsSuccess, submitted.ErrorMessage);
        Assert.Equal(PostTripComplaintStatus.AwaitingStaffReview, (await db.PostTripComplaints.SingleAsync()).Status);
        Assert.Single(db.ComplaintEvidence);
        Assert.Empty(storage.Deleted);
        Assert.True((await party.Handle(evidence, default)).IsSuccess);
        Assert.Single(db.ComplaintEvidence);
        Assert.True((await Handler(db).Handle(Command(), default)).IsSuccess);
        var reconsider = evidence with { OperationId = Guid.NewGuid(), RequestEventId = null, Reconsideration = true };
        Assert.True((await party.Handle(reconsider, default)).IsFailure); // Same file is not new evidence.
        var newProof = reconsider with { OperationId = Guid.NewGuid(), Files = [new("new.pdf", "application/pdf", "%PDF-new-proof"u8.ToArray())] };
        Assert.True((await party.Handle(newProof, default)).IsSuccess);
        Assert.Equal(PostTripComplaintStatus.Approved, (await db.PostTripComplaints.SingleAsync()).Status);
        Assert.Equal(3000, Assert.Single(db.DirectRefundCases).RefundAmountVnd);
        Assert.Single(db.ComplaintStaffReviewEvents.Where(e => e.Action == "ReconsiderationRequested"));
    }

    [Theory]
    [InlineData("Traveler", 2)]
    [InlineData("TourOperator", 3)]
    public async Task CompletedComplaintRefundMakesBothPartiesReadOnly(string role, long userId)
    {
        await using var db = await Seed();
        Assert.True((await Handler(db).Handle(Command(), default)).IsSuccess);
        var refund = await db.DirectRefundCases.SingleAsync();
        refund.Complete("TravelerConfirmed", null, Now);
        await db.SaveChangesAsync();
        var party = User(role, userId);
        var detail = await new GetStaffComplaintQueryHandler(db, party).Handle(new(1, true), default);
        Assert.True(detail.IsSuccess);
        Assert.True(detail.Value!.IsClosed);
        Assert.Empty(detail.Value!.AllowedActions);
        var storage = new MemoryEvidence();
        var submission = await new SubmitStaffComplaintEvidenceCommandHandler(db, party, Clock(), storage)
            .Handle(new(1, Guid.NewGuid(), null, "New evidence", [new("new.pdf", "application/pdf", "%PDF-new"u8.ToArray())], true), default);
        Assert.Equal(StaffComplaintAccess.Conflict, submission.ErrorCode);
        Assert.Empty(db.ComplaintEvidence);
        Assert.Single(db.ComplaintStaffReviewEvents);
        Assert.Equal(DirectRefundCase.StatusCompleted, refund.Status);
    }

    [Fact]
    public async Task ClosureMatchesComplaintReferenceAndSupportsLegacyProcessedRefunds()
    {
        await using var db = await Seed();
        var unrelated = DirectRefundCase.Create(1, DirectRefundCase.SourcePostTripComplaint, 1000, 10000, Now, 99);
        unrelated.Complete("CompletedAuto", null, Now);
        db.DirectRefundCases.Add(unrelated);
        await db.SaveChangesAsync();
        Assert.False(await StaffComplaintAccess.HasCompletedRefundAsync(db, 1, 1, default));
        var legacy = Refund.CreateForPostTripComplaint(1, 1, 1, 1000, Now);
        Set(legacy, nameof(Refund.Status), RefundStatus.Processed);
        db.Refunds.Add(legacy);
        await db.SaveChangesAsync();
        Assert.True(await StaffComplaintAccess.HasCompletedRefundAsync(db, 1, 1, default));
    }

    private static DecideStaffComplaintCommand Command() => new(1, "RefundPartial", "Service omitted", "Signed contract section 4", 3000, null, Convert.ToBase64String(Version), Guid.NewGuid());
    private static DecideStaffComplaintCommandHandler Handler(Context db, FakeCurrentUserService? user = null) => new(db, user ?? User(), Clock(), new NeverLegacy());
    private sealed class NeverLegacy : IComplaintRefundRequestCreator
    {
        public Task CreateForAcceptedComplaintAsync(long complaintId, long bookingId, decimal amount, DateTimeOffset requestedAtUtc, CancellationToken cancellationToken) => throw new InvalidOperationException("Direct refund must not use the legacy path.");
    }
    private static void Set(object value, string property, object data) => value.GetType().GetProperty(property)!.SetValue(value, data);
    private static async Task<Context> Seed()
    {
        var db = new Context(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), true)!;
        tour.Id = 1; Set(tour, nameof(Tour.OperatorUserId), 3L); Set(tour, nameof(Tour.Title), "Test tour");
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), true)!;
        schedule.Id = 1; Set(schedule, nameof(TourSchedule.TourId), 1L);
        var booking = Booking.Create(2, 1, 1, 10000, BookingStatus.Confirmed, PaymentStatus.Paid);
        booking.Id = 1; Set(booking, nameof(Booking.PaymentFlow), PaymentFlow.OperatorDirect);
        var payment = PaymentTransaction.Create(1, PaymentGateway.Mock, 10000, 1, Now);
        payment.MarkSuccess("test", null, Now, 0.1m);
        var complaint = PostTripComplaint.Create(1, 2, PostTripComplaintReasonCategory.Other, "Service not provided", Now, Now.AddDays(7), 10000);
        complaint.Id = 1; Set(complaint, nameof(PostTripComplaint.Status), PostTripComplaintStatus.AwaitingStaffReview); Set(complaint, nameof(PostTripComplaint.RowVersion), Version);
        db.Tours.Add(tour); db.TourSchedules.Add(schedule); db.Bookings.Add(booking); db.PaymentTransactions.Add(payment); db.PostTripComplaints.Add(complaint);
        await db.SaveChangesAsync();
        return db;
    }
    private sealed class Context(DbContextOptions<TestDbContext> options) : TestDbContext(options), IApplicationDbContext
    {
        public bool ReplayNextTransaction { get; set; }
        async Task<T> IApplicationDbContext.ExecuteInSerializableTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            var result = await base.ExecuteInSerializableTransactionAsync(operation, cancellationToken);
            if (ReplayNextTransaction) { ReplayNextTransaction = false; return await base.ExecuteInSerializableTransactionAsync(operation, cancellationToken); }
            return result;
        }
    }
    private sealed class MemoryEvidence : IComplaintEvidenceStorage
    {
        public List<string> Deleted { get; } = [];
        public Task<StoredComplaintEvidence?> StoreAsync(ComplaintEvidenceUpload upload, CancellationToken cancellationToken) => Task.FromResult<StoredComplaintEvidence?>(new(Guid.NewGuid().ToString(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(upload.Bytes)), upload.Bytes.Length));
        public Task<bool> DeleteAsync(string storageObjectKey, CancellationToken cancellationToken) { Deleted.Add(storageObjectKey); return Task.FromResult(true); }
        public Task<byte[]?> ReadAsync(string storageObjectKey, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public Uri? CreateTemporaryDownloadUrl(string storageObjectKey, string contentType, DateTimeOffset expiresAtUtc) => null;
    }
}