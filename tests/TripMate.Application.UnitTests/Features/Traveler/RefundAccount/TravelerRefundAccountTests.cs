using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.DirectRefunds.Create;
using TripMate.Application.Features.Bookings.DirectRefunds.Recipient;
using TripMate.Application.Features.Traveler.RefundAccount;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.RefundAccount;

public sealed class TravelerRefundAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private readonly FakePiiProtector protector = new();
    private static FakeCurrentUserService Traveler(long id = 10) => new() { UserId = id, Role = nameof(UserRole.Traveler) };
    private static FakeDateTimeProvider Clock() => new() { UtcNow = Now };
    private static Context Db() => new(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task SaveAndUpdateEncryptsValuesAndReadsOnlyOwnAccount()
    {
        await using var db = Db();
        var save = new SaveTravelerRefundAccountCommandHandler(db, Traveler(), Clock(), protector);
        Assert.True((await save.Handle(new(" VCB ", " 12345678 ", " Nguyen Van A "), default)).IsSuccess);
        var stored = await db.TravelerRefundAccounts.SingleAsync();
        Assert.NotEqual("12345678", stored.AccountNumberProtected);
        Assert.NotEqual("Nguyen Van A", stored.AccountHolderProtected);
        var get = await new GetTravelerRefundAccountQueryHandler(db, Traveler(), protector).Handle(new(), default);
        Assert.Equal("12345678", get.Value!.AccountNumber);
        Assert.Equal("VCB", get.Value.BankCode);
        Assert.Null((await new GetTravelerRefundAccountQueryHandler(db, Traveler(11), protector).Handle(new(), default)).Value);
        Assert.True((await save.Handle(new("TCB", "87654321", "Nguyen Van A"), default)).IsSuccess);
        Assert.Equal(1, await db.TravelerRefundAccounts.CountAsync());
    }

    [Fact]
    public async Task SavingDefaultConfirmsOnlyOwnCasesStillAwaitingRecipient()
    {
        await using var db = Db();
        var ownBooking = Booking.Create(10, 20, 1, 10000, BookingStatus.Confirmed, PaymentStatus.Paid);
        var otherBooking = Booking.Create(11, 20, 1, 10000, BookingStatus.Confirmed, PaymentStatus.Paid);
        db.Bookings.AddRange(ownBooking, otherBooking);
        await db.SaveChangesAsync();
        var waiting = DirectRefundCase.Create(ownBooking.Id, DirectRefundCase.SourcePolicyCancellation, 1000, 10000, Now);
        var confirmed = DirectRefundCase.Create(ownBooking.Id, DirectRefundCase.SourcePostTripComplaint, 1000, 10000, Now);
        confirmed.ConfirmRecipient("OLD", "11111111", "Original", Now);
        var foreign = DirectRefundCase.Create(otherBooking.Id, DirectRefundCase.SourcePolicyCancellation, 1000, 10000, Now);
        db.DirectRefundCases.AddRange(waiting, confirmed, foreign);
        await db.SaveChangesAsync();
        var result = await new SaveTravelerRefundAccountCommandHandler(db, Traveler(), Clock(), protector)
            .Handle(new("VCB", "12345678", "Holder"), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(DirectRefundCase.StatusAwaitingTransfer, waiting.Status);
        Assert.Equal("12345678", waiting.RecipientAccountNumber);
        Assert.Equal(Now, waiting.RecipientConfirmedAtUtc);
        Assert.NotNull(waiting.TransferDeadlineAtUtc);
        Assert.Equal("11111111", confirmed.RecipientAccountNumber);
        Assert.Equal(DirectRefundCase.StatusAwaitingRecipient, foreign.Status);
        Assert.Null(foreign.RecipientAccountNumber);
    }

    [Theory]
    [InlineData("", "123", "Holder")]
    [InlineData("VCB", "", "Holder")]
    [InlineData("VCB", "123", "")]
    public async Task RequiredFieldsAreValidated(string bank, string account, string holder)
    {
        await using var db = Db();
        var result = await new SaveTravelerRefundAccountCommandHandler(db, Traveler(), Clock(), protector).Handle(new(bank, account, holder), default);
        Assert.False(result.IsSuccess);
        Assert.Empty(db.TravelerRefundAccounts);
    }

    [Fact]
    public async Task StaffCannotReadOrWriteTravelerDefault()
    {
        await using var db = Db();
        var staff = new FakeCurrentUserService { UserId = 10, Role = nameof(UserRole.Staff) };
        Assert.False((await new SaveTravelerRefundAccountCommandHandler(db, staff, Clock(), protector).Handle(new("VCB", "123", "Holder"), default)).IsSuccess);
        Assert.False((await new GetTravelerRefundAccountQueryHandler(db, staff, protector).Handle(new(), default)).IsSuccess);
    }

    [Fact]
    public async Task ExistingRecipientBoundsAreEnforcedWithoutInventingBankFormats()
    {
        await using var db = Db();
        var handler = new SaveTravelerRefundAccountCommandHandler(db, Traveler(), Clock(), protector);
        Assert.False((await handler.Handle(new(new string('B', 21), "1", "Holder"), default)).IsSuccess);
        Assert.False((await handler.Handle(new("VCB", new string('1', 51), "Holder"), default)).IsSuccess);
        Assert.False((await handler.Handle(new("VCB", "1", new string('H', 201)), default)).IsSuccess);
        Assert.True((await handler.Handle(new("VCB", "1", "Holder"), default)).IsSuccess);
    }

    [Fact]
    public async Task ReadFailsSafelyWhenProtectedDataCannotBeDecrypted()
    {
        await using var db = Db();
        db.TravelerRefundAccounts.Add(TravelerRefundAccount.Create(10, "VCB", "not-valid-ciphertext", "not-valid-ciphertext", Now));
        await db.SaveChangesAsync();
        var result = await new GetTravelerRefundAccountQueryHandler(db, Traveler(), protector).Handle(new(), default);
        Assert.False(result.IsSuccess);
        Assert.Equal("direct_refund.storage_unavailable", result.ErrorCode);
    }

    [Fact]
    public async Task NewCaseSnapshotsDefaultAndLaterDefaultEditDoesNotAlterIt()
    {
        await using var db = Db();
        var booking = Booking.Create(10, 20, 1, 10000, BookingStatus.Confirmed, PaymentStatus.Paid);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var save = new SaveTravelerRefundAccountCommandHandler(db, Traveler(), Clock(), protector);
        await save.Handle(new("VCB", "12345678", "Holder"), default);
        var first = await DirectRefundCaseCreator.CreateAsync(db, booking.Id, DirectRefundCase.SourcePolicyCancellation, 1000, 10000, Now, null, default, protector);
        Assert.Equal(DirectRefundCase.StatusAwaitingTransfer, first.Value!.Status);
        Assert.Equal("12345678", first.Value.RecipientAccountNumber);
        await db.SaveChangesAsync();
        await save.Handle(new("TCB", "87654321", "Other Holder"), default);
        Assert.Equal("12345678", first.Value.RecipientAccountNumber);
        var second = await DirectRefundCaseCreator.CreateAsync(db, booking.Id, DirectRefundCase.SourcePostTripComplaint, 1000, 10000, Now, null, default, protector);
        Assert.Equal("87654321", second.Value!.RecipientAccountNumber);
    }

    [Fact]
    public async Task UnreadableDefaultStillCreatesRefundObligationAwaitingManualRecipient()
    {
        await using var db = Db();
        var booking = Booking.Create(10, 20, 1, 10000, BookingStatus.Confirmed, PaymentStatus.Paid);
        db.Bookings.Add(booking);
        db.TravelerRefundAccounts.Add(TravelerRefundAccount.Create(10, "VCB", "not-valid-ciphertext", "not-valid-ciphertext", Now));
        await db.SaveChangesAsync();
        var created = await DirectRefundCaseCreator.CreateAsync(db, booking.Id, DirectRefundCase.SourcePostTripComplaint, 1000, 10000, Now, null, default, protector);
        Assert.True(created.IsSuccess);
        Assert.Equal(DirectRefundCase.StatusAwaitingRecipient, created.Value!.Status);
        Assert.Null(created.Value.RecipientAccountNumber);
        Assert.Single(db.DirectRefundCases.Local);
    }

    [Fact]
    public async Task MissingDefaultPreservesManualRecipientFlowAndConfirmationSavesDefault()
    {
        await using var db = Db();
        var booking = Booking.Create(10, 20, 1, 10000, BookingStatus.Confirmed, PaymentStatus.Paid);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var created = await DirectRefundCaseCreator.CreateAsync(db, booking.Id, DirectRefundCase.SourcePolicyCancellation, 1000, 10000, Now, null, default, protector);
        Assert.Equal(DirectRefundCase.StatusAwaitingRecipient, created.Value!.Status);
        await db.SaveChangesAsync();
        typeof(DirectRefundCase).GetProperty(nameof(DirectRefundCase.RowVersion))!.SetValue(created.Value, new byte[] { 1 });
        var result = await new ConfirmDirectRefundRecipientCommandHandler(db, Traveler(), Clock(), protector)
            .Handle(new(created.Value.Id, "VCB", "12345678", "Holder", new byte[] { 1 }), default);
        Assert.True(result.IsSuccess);
        Assert.Equal("12345678", protector.Unprotect((await db.TravelerRefundAccounts.SingleAsync()).AccountNumberProtected));
    }

    private sealed class Context(DbContextOptions<TestDbContext> options) : TestDbContext(options), IApplicationDbContext
    {
    }
}