using FluentAssertions;

using TripMate.Application.Features.Bookings.DirectRefunds.Get;
using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.Get;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class AcceptedRefundTripHistoryTests
{
    [Theory]
    [InlineData(RefundStatus.Failed, RefundStatus.Processed, 7000)]
    [InlineData(RefundStatus.Processed, RefundStatus.Failed, 3000)]
    public async Task FailedLegacyAttemptCannotHideACompletedRefund(RefundStatus earlier, RefundStatus later, decimal expectedAmount)
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var now = TripHistoryTestData.BaseTime;
        var schedule = await data.AddScheduleAsync(now);
        var booking = await data.AddBookingAsync(7, schedule, BookingStatus.Cancelled, totalAmount: 10000);
        await data.AddRefundAsync(booking.Id, 1, earlier, 3000);
        await data.AddRefundAsync(booking.Id, 2, later, 7000);
        var detail = await new GetTravelerTripQueryHandler(db, new FakeDateTimeProvider { UtcNow = now })
            .Handle(new(7, TripRecordKinds.CommerceBooking, booking.Id), default);
        detail.Value!.Refund!.Status.Should().Be(nameof(RefundStatus.Processed));
        detail.Value.Refund.Amount.Should().Be(expectedAmount);
    }
    [Fact]
    public async Task CompletedLaterCaseDoesNotHideExistingRefundDispute()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var now = TripHistoryTestData.BaseTime;
        var schedule = await data.AddScheduleAsync(now);
        var booking = await data.AddBookingAsync(7, schedule, BookingStatus.Completed, totalAmount: 10000);
        var disputed = DirectRefundCase.Create(booking.Id, DirectRefundCase.SourcePostTripComplaint, 3000, 10000, now, 1);
        disputed.ConfirmRecipient("VCB", "12345678", "Test recipient", now);
        disputed.MarkTransferReported(now);
        disputed.Dispute(now);
        db.DirectRefundCases.Add(disputed);
        await db.SaveChangesAsync();
        var completed = DirectRefundCase.Create(booking.Id, DirectRefundCase.SourcePolicyCancellation, 1000, 10000, now);
        completed.Complete("TravelerConfirmed", null, now);
        db.DirectRefundCases.Add(completed);
        await db.SaveChangesAsync();
        var detail = await new GetTravelerTripQueryHandler(db, new FakeDateTimeProvider { UtcNow = now })
            .Handle(new(7, TripRecordKinds.CommerceBooking, booking.Id), default);
        detail.Value!.Status.Should().Be("RefundDisputed");
        detail.Value.Refund!.Status.Should().Be("Disputed");
        detail.Value.Refund.Amount.Should().Be(3000);
        var target = await new GetDirectRefundCaseByBookingQueryHandler(db, new FakeCurrentUserService { UserId = 7, Role = "Traveler" })
            .Handle(new(booking.Id), default);
        target.Value!.CaseId.Should().Be(disputed.Id);
    }
    [Theory]
    [InlineData(RefundStatus.Processed, RefundStatus.Rejected, "Processed")]
    [InlineData(RefundStatus.Processing, RefundStatus.Processed, "Processing")]
    [InlineData(RefundStatus.Pending, RefundStatus.Processed, "Pending")]
    public async Task LaterLegacyAttemptDoesNotHideThePriorityRefund(RefundStatus earlier, RefundStatus later, string expected)
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var now = TripHistoryTestData.BaseTime;
        var schedule = await data.AddScheduleAsync(now);
        var booking = await data.AddBookingAsync(7, schedule, BookingStatus.Completed, totalAmount: 10000);
        await data.AddRefundAsync(booking.Id, 1, earlier, 3000);
        await data.AddRefundAsync(booking.Id, 2, later, 7000);
        var detail = await new GetTravelerTripQueryHandler(db, new FakeDateTimeProvider { UtcNow = now })
            .Handle(new(7, TripRecordKinds.CommerceBooking, booking.Id), default);
        detail.Value!.Refund!.Status.Should().Be(expected);
        detail.Value.Refund.Amount.Should().Be(3000);
    }
    [Theory]
    [InlineData(3000, "PartiallyRefunded")]
    [InlineData(10000, "Refunded")]
    public async Task OnlyCompletedTransfersShowRefundedStatus(long amount, string expected)
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var now = TripHistoryTestData.BaseTime;
        var schedule = await data.AddScheduleAsync(now);
        var booking = await data.AddBookingAsync(7, schedule, BookingStatus.Completed, totalAmount: 10000);
        var refund = DirectRefundCase.Create(booking.Id, DirectRefundCase.SourcePostTripComplaint, amount, 10000, now, 1);
        refund.Complete("TravelerConfirmed", null, now);
        db.DirectRefundCases.Add(refund);
        await db.SaveChangesAsync();
        var detail = await new GetTravelerTripQueryHandler(db, new FakeDateTimeProvider { UtcNow = now })
            .Handle(new(7, TripRecordKinds.CommerceBooking, booking.Id), default);
        detail.Value!.Status.Should().Be(expected);
        detail.Value.Actions.CanViewRefund.Should().BeTrue();
    }
    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Completed)]
    public async Task AcceptedDirectRefundMovesOnlyHistoryToCancelledAndOffersRefundDetail(BookingStatus original)
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var now = TripHistoryTestData.BaseTime;
        var schedule = await data.AddScheduleAsync(now);
        var booking = await data.AddBookingAsync(7, schedule, original, totalAmount: 10000);
        var refund = DirectRefundCase.Create(booking.Id, DirectRefundCase.SourcePostTripComplaint, 3000, 10000, now, 1);
        db.DirectRefundCases.Add(refund);
        await db.SaveChangesAsync();
        foreach (string tab in ListTravelerTripsQuery.AllowedTabs)
        {
            var window = await TripBookingStream.ReadAsync(db, new(7, tab, new(null, null)), 20, default);
            window.TotalCount.Should().Be(tab == "Cancelled" ? 1 : 0);
        }
        var detail = await new GetTravelerTripQueryHandler(db, new FakeDateTimeProvider { UtcNow = now })
            .Handle(new(7, TripRecordKinds.CommerceBooking, booking.Id), default);
        detail.IsSuccess.Should().BeTrue();
        detail.Value!.Tab.Should().Be("Cancelled");
        detail.Value.Status.Should().Be("RefundPending");
        detail.Value.Actions.CanViewRefund.Should().BeTrue();
        detail.Value.Actions.CanViewETicket.Should().BeFalse();
        detail.Value.Actions.CanReview.Should().BeFalse();
        detail.Value.Refund.Should().NotBeNull();
        detail.Value.Refund!.Amount.Should().Be(3000);
        booking.Status.Should().Be(original);
    }

    [Fact]
    public async Task RejectedLegacyRefundDoesNotMoveCompletedBooking()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var schedule = await data.AddScheduleAsync(TripHistoryTestData.BaseTime);
        var booking = await data.AddBookingAsync(7, schedule, BookingStatus.Completed);
        await data.AddRefundAsync(booking.Id, 1, status: RefundStatus.Rejected);
        var window = await TripBookingStream.ReadAsync(db, new(7, "Completed", new(null, null)), 20, default);
        window.TotalCount.Should().Be(1);
    }
}