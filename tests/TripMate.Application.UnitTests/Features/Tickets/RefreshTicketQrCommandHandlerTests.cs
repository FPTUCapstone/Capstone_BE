using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Application.Features.Tickets.Refresh;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.Features.Tickets.TicketTestData;

namespace TripMate.Application.UnitTests.Features.Tickets;

public class RefreshTicketQrCommandHandlerTests
{
    [Fact]
    public async Task Refresh_RevokesTheActiveQrAndReturnsANewOne()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        var old = await data.AddQrAsync(ticket.Id, "old-token", TicketQrCodeStatus.Active, InWindow.AddHours(-2));

        var result = await Handle(data, ticket.Id, TravelerId, InWindow);

        result.IsSuccess.Should().BeTrue();
        var codes = await data.QrCodesAsync(ticket.Id);
        codes.Should().HaveCount(2);
        codes.Single(code => code.Id == old.Id).Status.Should().Be(TicketQrCodeStatus.Revoked);
        var replacement = codes.Single(code => code.Status == TicketQrCodeStatus.Active);
        replacement.QrToken.Should().NotBe("old-token");
        replacement.IssuedAtUtc.Should().Be(InWindow);
        result.Value.TicketStatus.Should().Be(TicketDisplayStatus.Valid);
        result.Value.Qr.Should().Be(new TicketQrResponse(
            data.Codec.ContentFor(replacement.QrToken, replacement.Signature), InWindow));
    }

    [Fact]
    public async Task Refresh_BeforeTheDepartureDate_IsAllowed()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        await data.AddQrAsync(ticket.Id, "old-token", TicketQrCodeStatus.Active, BeforeWindow.AddDays(-1));

        var result = await Handle(data, ticket.Id, TravelerId, BeforeWindow);

        result.Value.TicketStatus.Should().Be(TicketDisplayStatus.NotYetActive);
        (await data.QrCodesAsync(ticket.Id)).Count(code => code.Status == TicketQrCodeStatus.Active).Should().Be(1);
    }

    [Fact]
    public async Task Refresh_WithoutAnActiveQrYet_IssuesOne()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        var result = await Handle(data, ticket.Id, TravelerId, InWindow);

        result.Value.Qr.Should().NotBeNull();
        (await data.QrCodesAsync(ticket.Id)).Should().ContainSingle()
            .Which.Status.Should().Be(TicketQrCodeStatus.Active);
    }

    [Theory]
    [InlineData(BookingStatus.Completed, PaymentStatus.Paid, TicketStatus.Used, TicketDisplayStatus.Used)]
    [InlineData(BookingStatus.Cancelled, PaymentStatus.Refunded, TicketStatus.Cancelled, TicketDisplayStatus.Cancelled)]
    [InlineData(BookingStatus.Confirmed, PaymentStatus.Paid, TicketStatus.Expired, TicketDisplayStatus.Expired)]
    public async Task Refresh_OfAnUnusableTicket_IsRefusedWithItsStatus(BookingStatus bookingStatus,
        PaymentStatus paymentStatus, TicketStatus ticketStatus, TicketDisplayStatus expected)
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(bookingStatus, paymentStatus), ticketStatus);

        var result = await Handle(data, ticket.Id, TravelerId, InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.NotRefreshable);
        result.ErrorMetadata[TicketErrorCodes.TicketStatusMetadataKey].Should().Be(expected.ToString());
        (await data.QrCodesAsync(ticket.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_AfterTheDepartureDate_IsRefusedAsExpiredAndKeepsTheOldQr()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        await data.AddQrAsync(ticket.Id, "old-token", TicketQrCodeStatus.Active, InWindow);

        var result = await Handle(data, ticket.Id, TravelerId, AfterWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.NotRefreshable);
        result.ErrorMetadata[TicketErrorCodes.TicketStatusMetadataKey]
            .Should().Be(nameof(TicketDisplayStatus.Expired));
        (await data.QrCodesAsync(ticket.Id)).Should().ContainSingle()
            .Which.Status.Should().Be(TicketQrCodeStatus.Active);
    }

    [Fact]
    public async Task Refresh_OfAnotherTravelersTicket_IsForbiddenAndChangesNothing()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        await data.AddQrAsync(ticket.Id, "old-token", TicketQrCodeStatus.Active, InWindow.AddHours(-1));

        var result = await Handle(data, ticket.Id, OtherTravelerId, InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.Forbidden);
        (await data.QrCodesAsync(ticket.Id)).Should().ContainSingle()
            .Which.QrToken.Should().Be("old-token");
    }

    [Fact]
    public async Task Refresh_OfAnUnknownTicket_IsNotFound()
    {
        var data = new TicketTestData();

        var result = await Handle(data, 404, TravelerId, InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.NotFound);
    }

    [Fact]
    public async Task Refresh_WhenTheBookingIsNotConfirmedByPayment_IsRefused()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(
            await data.AddBookingAsync(BookingStatus.PendingPayment, PaymentStatus.Unpaid));

        var result = await Handle(data, ticket.Id, TravelerId, InWindow);

        result.ErrorCode.Should().Be(TicketErrorCodes.BookingNotConfirmed);
    }

    [Fact]
    public async Task Refresh_LosingTheRaceToAnotherRefresh_ReturnsTheWinnersQr()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        var old = await data.AddQrAsync(ticket.Id, "old-token", TicketQrCodeStatus.Active, InWindow.AddHours(-1));
        var saves = 0;
        data.Db.SaveChangesFailureFactory = () =>
        {
            if (++saves < 2)
                return null;

            data.Db.SaveChangesFailureFactory = null;
            using var concurrent = data.NewContext();
            concurrent.TicketQrCodes.Add(TicketQrCode.Issue(ticket.Id, "winner-token", "winner-signature", InWindow));
            concurrent.SaveChanges();
            return new DbUpdateException("Cannot insert duplicate key row with unique index 'UX_TicketQrCodes_OneActivePerTicket'.");
        };

        var result = await Handle(data, ticket.Id, TravelerId, InWindow);

        result.IsSuccess.Should().BeTrue();
        result.Value.Qr!.Content.Should().Be(data.Codec.ContentFor("winner-token", "winner-signature"));
        (await data.QrCodesAsync(ticket.Id)).Single(code => code.Id == old.Id).Status
            .Should().Be(TicketQrCodeStatus.Revoked);
    }

    private static Task<Result<TicketResponse>> Handle(TicketTestData data, long ticketId, long travelerId,
        DateTimeOffset now) =>
        new RefreshTicketQrCommandHandler(data.Db, data.Codec, data.Classifier, ClockAt(now))
            .Handle(new RefreshTicketQrCommand(travelerId, ticketId), CancellationToken.None);
}
