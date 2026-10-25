using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Application.Features.Tickets.Get;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.Features.Tickets.TicketTestData;

namespace TripMate.Application.UnitTests.Features.Tickets;

public class GetTravelerTicketQueryHandlerTests
{
    [Fact]
    public async Task ByTicket_InTheWindow_ReturnsTheContractAndIssuesOneActiveQr()
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid, adults: 2, children: 1);
        var ticket = await data.AddTicketAsync(booking);

        var result = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), InWindow);

        result.IsSuccess.Should().BeTrue();
        var response = result.Value;
        response.TicketId.Should().Be(ticket.Id);
        response.TicketCode.Should().Be(Ticket.CodeFor(booking.Id));
        response.BookingId.Should().Be(booking.Id);
        response.BookingCode.Should().Be(booking.BookingCode);
        response.TicketStatus.Should().Be(TicketDisplayStatus.Valid);
        response.Tour.Should().Be(new TicketTourResponse(45, "Hoi An Lantern Tour"));
        response.OperatorName.Should().Be(OperatorName);
        response.Schedule.Should().Be(new TicketScheduleResponse(Departure, Departure.AddHours(9), "Old Town gate"));
        response.ParticipantCount.Should().Be(3);
        response.Participants.Should().Be(new TicketParticipantsResponse(2, 1, 0));
        response.Validity.Should().Be(new TicketValidityResponse(new DateOnly(2026, 10, 30),
            TicketValidityWindow.TimeZoneId, new DateTimeOffset(2026, 10, 29, 17, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 30, 17, 0, 0, TimeSpan.Zero).AddTicks(-1)));
        response.CheckedInAtUtc.Should().BeNull();

        var code = (await data.QrCodesAsync(ticket.Id)).Should().ContainSingle().Subject;
        code.Status.Should().Be(TicketQrCodeStatus.Active);
        code.IssuedAtUtc.Should().Be(InWindow);
        response.Qr.Should().Be(new TicketQrResponse(data.Codec.ContentFor(code.QrToken, code.Signature), InWindow));
    }

    [Fact]
    public async Task ByTicket_ViewedAgain_ReturnsTheSameQrAndChangesNoStatus()
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var ticket = await data.AddTicketAsync(booking);

        var first = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), InWindow);
        var second = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), InWindow.AddMinutes(5));

        second.Value.Qr.Should().Be(first.Value.Qr);
        (await data.QrCodesAsync(ticket.Id)).Should().ContainSingle();
        await using var context = data.NewContext();
        (await context.Tickets.SingleAsync(candidate => candidate.Id == ticket.Id)).Status.Should().Be(TicketStatus.Valid);
        (await context.Bookings.SingleAsync(candidate => candidate.Id == booking.Id)).Status
            .Should().Be(BookingStatus.Confirmed);
    }

    [Fact]
    public async Task ByTicket_BeforeTheDepartureDate_IsNotYetActiveAndStillReturnsTheQr()
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var ticket = await data.AddTicketAsync(booking);

        var result = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), BeforeWindow);

        result.Value.TicketStatus.Should().Be(TicketDisplayStatus.NotYetActive);
        result.Value.Qr.Should().NotBeNull();
    }

    [Theory]
    [InlineData(BookingStatus.Completed, PaymentStatus.Paid, TicketStatus.Used, TicketDisplayStatus.Used)]
    [InlineData(BookingStatus.Cancelled, PaymentStatus.Refunded, TicketStatus.Cancelled, TicketDisplayStatus.Cancelled)]
    [InlineData(BookingStatus.Cancelled, PaymentStatus.Paid, TicketStatus.Valid, TicketDisplayStatus.Cancelled)]
    [InlineData(BookingStatus.Confirmed, PaymentStatus.Paid, TicketStatus.Expired, TicketDisplayStatus.Expired)]
    public async Task ByTicket_WhenTheTicketCannotBeUsed_ReturnsNoQrAndIssuesNone(BookingStatus bookingStatus,
        PaymentStatus paymentStatus, TicketStatus ticketStatus, TicketDisplayStatus expected)
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(bookingStatus, paymentStatus);
        var checkedInAt = ticketStatus == TicketStatus.Used ? InWindow.AddMinutes(-10) : (DateTimeOffset?)null;
        var ticket = await data.AddTicketAsync(booking, ticketStatus, checkedInAt);

        var result = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), InWindow);

        result.IsSuccess.Should().BeTrue();
        result.Value.TicketStatus.Should().Be(expected);
        result.Value.Qr.Should().BeNull();
        result.Value.CheckedInAtUtc.Should().Be(checkedInAt);
        (await data.QrCodesAsync(ticket.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ByTicket_AfterTheDepartureDate_IsExpiredWithoutQr()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        var result = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), AfterWindow);

        result.Value.TicketStatus.Should().Be(TicketDisplayStatus.Expired);
        result.Value.Qr.Should().BeNull();
    }

    [Fact]
    public async Task ByTicket_WithoutAMeetingPoint_ReturnsNull()
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid, meetingPoint: null);
        var ticket = await data.AddTicketAsync(booking);

        var result = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), InWindow);

        result.Value.Schedule.MeetingPoint.Should().BeNull();
    }

    [Fact]
    public async Task ByTicket_OfAnotherTraveler_IsForbiddenAndIssuesNothing()
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var ticket = await data.AddTicketAsync(booking);

        var result = await Handle(data, GetTravelerTicketQuery.ForTicket(OtherTravelerId, ticket.Id), InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.Forbidden);
        (await data.QrCodesAsync(ticket.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ByTicket_Unknown_IsNotFound()
    {
        var data = new TicketTestData();

        var result = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, 404), InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.NotFound);
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment, PaymentStatus.Unpaid)]
    [InlineData(BookingStatus.Expired, PaymentStatus.Unpaid)]
    [InlineData(BookingStatus.Confirmed, PaymentStatus.Unpaid)]
    public async Task ByTicket_WhenTheBookingIsNotConfirmedByPayment_IsRefused(BookingStatus status,
        PaymentStatus paymentStatus)
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(status, paymentStatus));

        var result = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.BookingNotConfirmed);
        (await data.QrCodesAsync(ticket.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ByBooking_ReturnsTheSameBodyAsByTicket()
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var ticket = await data.AddTicketAsync(booking);

        var byBooking = await Handle(data, GetTravelerTicketQuery.ForBooking(TravelerId, booking.Id), InWindow);
        var byTicket = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), InWindow);

        byBooking.Value.Should().BeEquivalentTo(byTicket.Value);
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment, PaymentStatus.Unpaid)]
    [InlineData(BookingStatus.Expired, PaymentStatus.Unpaid)]
    [InlineData(BookingStatus.Cancelled, PaymentStatus.Unpaid)]
    public async Task ByBooking_NotConfirmedByPayment_IsRefused(BookingStatus status, PaymentStatus paymentStatus)
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(status, paymentStatus);

        var result = await Handle(data, GetTravelerTicketQuery.ForBooking(TravelerId, booking.Id), InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.BookingNotConfirmed);
    }

    [Fact]
    public async Task ByBooking_PaidWithoutATicket_IsNotIssuedAndCreatesNothing()
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);

        var result = await Handle(data, GetTravelerTicketQuery.ForBooking(TravelerId, booking.Id), InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.NotIssued);
        await using var context = data.NewContext();
        (await context.Tickets.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ByBooking_CancelledWithATicket_ReturnsCancelled()
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(BookingStatus.Cancelled, PaymentStatus.Refunded);
        await data.AddTicketAsync(booking, TicketStatus.Cancelled);

        var result = await Handle(data, GetTravelerTicketQuery.ForBooking(TravelerId, booking.Id), InWindow);

        result.Value.TicketStatus.Should().Be(TicketDisplayStatus.Cancelled);
        result.Value.Qr.Should().BeNull();
    }

    [Fact]
    public async Task ByBooking_OfAnotherTraveler_IsForbiddenBeforeAnyStateCheck()
    {
        var data = new TicketTestData();
        var booking = await data.AddBookingAsync(BookingStatus.PendingPayment, PaymentStatus.Unpaid);

        var result = await Handle(data, GetTravelerTicketQuery.ForBooking(OtherTravelerId, booking.Id), InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.Forbidden);
    }

    [Fact]
    public async Task ByBooking_Unknown_IsNotFound()
    {
        var data = new TicketTestData();

        var result = await Handle(data, GetTravelerTicketQuery.ForBooking(TravelerId, 404), InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.NotFound);
    }

    [Fact]
    public async Task FirstView_LosingTheRaceToAnotherRequest_ReturnsTheWinnersQr()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        TicketQrCode? winner = null;
        data.Db.SaveChangesFailureFactory = () =>
        {
            data.Db.SaveChangesFailureFactory = null;
            using var concurrent = data.NewContext();
            winner = TicketQrCode.Issue(ticket.Id, "winner-token", "winner-signature", InWindow.AddTicks(-1));
            concurrent.TicketQrCodes.Add(winner);
            concurrent.SaveChanges();
            return new DbUpdateException("Cannot insert duplicate key row with unique index 'UX_TicketQrCodes_OneActivePerTicket'.");
        };

        var result = await Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), InWindow);

        result.IsSuccess.Should().BeTrue();
        result.Value.Qr!.Content.Should().Be(data.Codec.ContentFor("winner-token", "winner-signature"));
        (await data.QrCodesAsync(ticket.Id)).Should().ContainSingle().Which.QrToken.Should().Be("winner-token");
    }

    [Fact]
    public async Task FirstView_WithAnUnrelatedSaveFailure_Rethrows()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        data.Db.SaveChangesFailureFactory = () => new DbUpdateException("Some other failure.");

        var action = () => Handle(data, GetTravelerTicketQuery.ForTicket(TravelerId, ticket.Id), InWindow);

        await action.Should().ThrowAsync<DbUpdateException>();
    }

    private static Task<Result<TicketResponse>> Handle(TicketTestData data, GetTravelerTicketQuery query,
        DateTimeOffset now) =>
        new GetTravelerTicketQueryHandler(data.Db, data.Codec, data.Classifier, ClockAt(now))
            .Handle(query, CancellationToken.None);
}