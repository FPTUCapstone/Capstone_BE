using FluentAssertions;

using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.Get;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class GetTravelerTripQueryHandlerTests
{
    private const long TravelerId = 7;
    private const long OtherTravelerId = 8;

    private static readonly DateTimeOffset Departure = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Departure.AddDays(90);

    private static Task<TripMate.Application.Common.Models.Result<TripDetailResponse>> Handle(
        TripHistoryTestData data, string kind, long id, long travelerId = TravelerId) =>
        new GetTravelerTripQueryHandler(data.Db, new FakeDateTimeProvider { UtcNow = Now })
            .Handle(new GetTravelerTripQuery(travelerId, kind, id), CancellationToken.None);

    private static async Task<TripHistoryTestData> NewDataAsync()
    {
        var data = new TripHistoryTestData(TripHistoryTestData.CreateDb());
        await data.AddTravelerAsync(TravelerId);
        await data.AddTravelerAsync(OtherTravelerId);
        return data;
    }

    // --- tour booking ---

    [Fact]
    public async Task Booking_Completed_ReturnsTheCardAndTheTourDetail()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure, "Hoi An Lantern Tour", "Hoi An");
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed, quantity: 4);
        await data.AddParticipantsAsync(booking.Id, adults: 2, children: 1, infants: 1);
        await data.AddTicketAsync(booking.Id, TicketStatus.Used, id: 12);

        var result = await Handle(data, TripRecordKinds.CommerceBooking, booking.Id);

        result.IsSuccess.Should().BeTrue();
        var detail = result.Value;
        detail.Kind.Should().Be(TripRecordKinds.CommerceBooking);
        detail.Id.Should().Be(booking.Id);
        detail.Tab.Should().Be(ListTravelerTripsQuery.TabCompleted);
        detail.Status.Should().Be("Completed");
        detail.Title.Should().Be("Hoi An Lantern Tour");
        detail.DepartureAtUtc.Should().Be(Departure);
        detail.ReviewableRecord!.Id.Should().Be(booking.Id);
        detail.Actions.Should().Be(new TripActionsResponse(true, true, 12, false));
        detail.Tour.Should().Be(new TripTourInfo("Hoi An Lantern Tour", "Hoi An"));
        detail.Schedule.Should().Be(new TripScheduleInfo(Departure, Departure.AddHours(8), "Old Town gate"));
        detail.Participants.Should().Be(new TripParticipantCounts(2, 1, 1));
        detail.Cancellation.Should().BeNull();
        detail.Refund.Should().BeNull();
        detail.Stops.Should().BeNull();
        detail.Session.Should().BeNull();
    }

    [Fact]
    public async Task Booking_WithNoParticipantRows_HasZeroCounts()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Confirmed);

        var detail = (await Handle(data, TripRecordKinds.CommerceBooking, booking.Id)).Value;

        detail.Tab.Should().Be(ListTravelerTripsQuery.TabUpcoming);
        detail.Participants.Should().Be(new TripParticipantCounts(0, 0, 0));
    }

    [Fact]
    public async Task Booking_Cancelled_ReturnsTheCancellationAndTheLatestRefund()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Cancelled);
        var cancelledAt = Departure.AddDays(-3);
        await data.MarkCancelledAsync(booking, cancelledAt, "Change of plans");
        await data.AddPaymentAsync(booking.Id, id: 100, PaymentGateway.PayOS);
        await data.AddPaymentAsync(booking.Id, id: 101, PaymentGateway.VNPAY);
        await data.AddRefundAsync(booking.Id, id: 2, RefundStatus.Failed, amount: 500_000m, transactionId: 100);
        await data.AddRefundAsync(booking.Id, id: 5, RefundStatus.Processed, amount: 800_000m, transactionId: 101);
        await data.AddRefundAsync(booking.Id, id: 3, RefundStatus.Pending, amount: 100_000m, transactionId: 100);

        var detail = (await Handle(data, TripRecordKinds.CommerceBooking, booking.Id)).Value;

        detail.Tab.Should().Be(ListTravelerTripsQuery.TabCancelled);
        detail.Actions.CanViewRefund.Should().BeTrue();
        detail.Cancellation.Should().Be(new TripCancellationInfo(cancelledAt, "Change of plans"));
        detail.Refund.Should().Be(new TripRefundInfo("Processed", 800_000m, "VNPAY"));
    }

    [Fact]
    public async Task Booking_RefundWithoutAnOriginalTransaction_HasNoChannel()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Cancelled);
        await data.AddRefundAsync(booking.Id, id: 1, RefundStatus.Pending, amount: 300_000m, transactionId: null);

        var detail = (await Handle(data, TripRecordKinds.CommerceBooking, booking.Id)).Value;

        detail.Refund.Should().Be(new TripRefundInfo("Pending", 300_000m, null));
    }

    [Fact]
    public async Task Booking_RefundPointingAtAMissingTransaction_HasNoChannel()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Cancelled);
        await data.AddRefundAsync(booking.Id, id: 1, RefundStatus.Processed, transactionId: 999);

        (await Handle(data, TripRecordKinds.CommerceBooking, booking.Id)).Value.Refund!.Channel.Should().BeNull();
    }

    [Fact]
    public async Task Booking_CancelledWithoutARefund_HasNoRefundObjectAndCannotViewRefund()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Cancelled);
        await data.MarkCancelledAsync(booking, Departure.AddDays(-1), null);

        var detail = (await Handle(data, TripRecordKinds.CommerceBooking, booking.Id)).Value;

        detail.Refund.Should().BeNull();
        detail.Actions.CanViewRefund.Should().BeFalse();
        detail.Cancellation.Should().Be(new TripCancellationInfo(Departure.AddDays(-1), null));
    }

    [Fact]
    public async Task Booking_Expired_IsInTheCancelledTabWithoutCancellationDetail()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Expired);
        await data.AddRefundAsync(booking.Id, id: 1);

        var detail = (await Handle(data, TripRecordKinds.CommerceBooking, booking.Id)).Value;

        detail.Tab.Should().Be(ListTravelerTripsQuery.TabCancelled);
        detail.Cancellation.Should().BeNull();
        detail.Refund.Should().BeNull();
    }

    [Fact]
    public async Task Booking_OfAnotherTraveler_IsNotFound()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var foreign = await data.AddBookingAsync(OtherTravelerId, schedule, BookingStatus.Completed);

        var result = await Handle(data, TripRecordKinds.CommerceBooking, foreign.Id);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TripHistoryErrorCodes.RecordNotFound);
    }

    [Fact]
    public async Task Booking_ThatDoesNotExist_IsNotFound()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;

        (await Handle(data, TripRecordKinds.CommerceBooking, 12345)).ErrorCode
            .Should().Be(TripHistoryErrorCodes.RecordNotFound);
    }

    [Fact]
    public async Task Booking_IdIsNotInterchangeableWithAnItineraryId()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId);

        // Same numeric id, other namespace: there is no booking with this id.
        (await Handle(data, TripRecordKinds.CommerceBooking, itinerary.Id)).ErrorCode
            .Should().Be(TripHistoryErrorCodes.RecordNotFound);
    }

    // --- self-planned itinerary ---

    [Fact]
    public async Task Itinerary_Completed_ReturnsStopsWithVisitDataFromTheRepresentativeSession()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var validFrom = Departure.AddDays(-2);
        var itinerary = await data.AddItineraryAsync(
            TravelerId, visitCount: 3, validFromUtc: validFrom, validToUtc: validFrom.AddDays(3));
        // An older completed session must be ignored in favour of the latest one (T1).
        await data.AddSessionAsync(itinerary, TravelerId, Departure, Departure.AddHours(2), reachedItems: 3);
        var representative = await data.AddSessionAsync(
            itinerary, TravelerId, Departure.AddDays(1), Departure.AddDays(1).AddHours(6), reachedItems: 1, skippedItems: 1);

        var result = await Handle(data, TripRecordKinds.Itinerary, itinerary.Id);

        result.IsSuccess.Should().BeTrue();
        var detail = result.Value;
        detail.Kind.Should().Be(TripRecordKinds.Itinerary);
        detail.Tab.Should().Be(ListTravelerTripsQuery.TabCompleted);
        detail.Status.Should().Be("Completed");
        detail.DepartureAtUtc.Should().Be(Departure.AddDays(1));
        detail.StopCount.Should().Be(3);
        detail.VisitedCount.Should().Be(1);
        detail.ReviewableRecord.Should().BeNull();
        detail.ValidFromUtc.Should().Be(validFrom);
        detail.ValidToUtc.Should().Be(validFrom.AddDays(3));
        detail.Session.Should().Be(new TripSessionInfo(
            Departure.AddDays(1), Departure.AddDays(1).AddHours(6), TripSession.TravelerStoppedReason));
        var stops = detail.Stops!.ToArray();
        stops.Select(stop => stop.SequenceNo).Should().Equal(1, 2, 3);
        detail.Stops!.Select(stop => stop.VisitStatus).Should().Equal(
            TripSessionItem.ReachedStatus, TripSessionItem.SkippedStatus, TripSessionItem.PendingStatus);
        stops[0].ReachedAtUtc.Should().NotBeNull();
        stops[0].SkippedAtUtc.Should().BeNull();
        stops[1].SkippedAtUtc.Should().NotBeNull();
        stops[1].ReachedAtUtc.Should().BeNull();
        stops[2].ReachedAtUtc.Should().BeNull();
        stops[0].PoiName.Should().Be("Dragon Bridge");
        stops[0].PlannedArrivalUtc.Should().NotBeNull();
        representative.Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Itinerary_CompletedByExpiry_ReportsTheExpiryAsTheEndAndReason()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, visitCount: 2);
        var session = await data.AddSessionAsync(itinerary, TravelerId, Departure, reachedItems: 1, lifetime: TimeSpan.FromDays(2));

        var detail = (await Handle(data, TripRecordKinds.Itinerary, itinerary.Id)).Value;

        detail.Tab.Should().Be(ListTravelerTripsQuery.TabCompleted);
        detail.Session.Should().Be(new TripSessionInfo(Departure, session.ExpiresAtUtc, TripSession.ExpiredReason));
        detail.Stops!.Select(stop => stop.VisitStatus).Should().Equal(
            TripSessionItem.ReachedStatus, TripSessionItem.PendingStatus);
    }

    [Theory]
    [InlineData(Itinerary.ActiveStatus, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(Itinerary.CancelledStatus, ListTravelerTripsQuery.TabCancelled)]
    public async Task Itinerary_NotCompleted_ReturnsPlannedStopsWithoutVisitDataOrSession(string status, string tab)
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, status, visitCount: 3, validFromUtc: Departure);
        // An open (not yet expired) session does not complete the trip and is not reported.
        await data.AddSessionAsync(itinerary, TravelerId, Departure.AddDays(80));

        var detail = (await Handle(data, TripRecordKinds.Itinerary, itinerary.Id)).Value;

        detail.Tab.Should().Be(tab);
        detail.Status.Should().Be(status);
        detail.StopCount.Should().Be(3);
        detail.VisitedCount.Should().BeNull();
        detail.Session.Should().BeNull();
        var stops = detail.Stops!.ToArray();
        stops.Select(stop => stop.SequenceNo).Should().Equal(1, 2, 3);
        detail.Stops!.Should().OnlyContain(stop =>
            stop.VisitStatus == null && stop.ReachedAtUtc == null && stop.SkippedAtUtc == null
            && stop.PoiName == "Dragon Bridge" && stop.PlannedArrivalUtc != null);
    }

    [Fact]
    public async Task Itinerary_Draft_IsNotFound()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var draft = await data.AddItineraryAsync(TravelerId, Itinerary.DraftStatus);

        (await Handle(data, TripRecordKinds.Itinerary, draft.Id)).ErrorCode
            .Should().Be(TripHistoryErrorCodes.RecordNotFound);
    }

    [Fact]
    public async Task Itinerary_CompletedStatusWithoutASession_IsNotFound()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, Itinerary.CompletedStatus);

        (await Handle(data, TripRecordKinds.Itinerary, itinerary.Id)).ErrorCode
            .Should().Be(TripHistoryErrorCodes.RecordNotFound);
    }

    [Fact]
    public async Task Itinerary_BookedTour_IsNotFound()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, sourceType: Itinerary.BookedTourSourceType);

        (await Handle(data, TripRecordKinds.Itinerary, itinerary.Id)).ErrorCode
            .Should().Be(TripHistoryErrorCodes.RecordNotFound);
    }

    [Fact]
    public async Task Itinerary_OfAnotherTraveler_IsNotFound()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var foreign = await data.AddItineraryAsync(OtherTravelerId);
        await data.AddSessionAsync(foreign, OtherTravelerId, Departure, Departure.AddHours(3));

        (await Handle(data, TripRecordKinds.Itinerary, foreign.Id)).ErrorCode
            .Should().Be(TripHistoryErrorCodes.RecordNotFound);
    }

    [Fact]
    public async Task Itinerary_AnotherTravelersSessionDoesNotCompleteMyTrip()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var mine = await data.AddItineraryAsync(TravelerId, validFromUtc: Departure);
        await data.AddSessionAsync(mine, OtherTravelerId, Departure, Departure.AddHours(3));

        var detail = (await Handle(data, TripRecordKinds.Itinerary, mine.Id)).Value;

        detail.Tab.Should().Be(ListTravelerTripsQuery.TabUpcoming);
        detail.Session.Should().BeNull();
    }

    [Fact]
    public async Task Itinerary_AgreesWithTheListCard()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, visitCount: 3);
        await data.AddSessionAsync(itinerary, TravelerId, Departure, Departure.AddHours(5), reachedItems: 2);

        var detail = (await Handle(data, TripRecordKinds.Itinerary, itinerary.Id)).Value;
        var list = await new ListTravelerTripsQueryHandler(db, new FakeDateTimeProvider { UtcNow = Now })
            .Handle(new ListTravelerTripsQuery(TravelerId, ListTravelerTripsQuery.TabCompleted, null, null, null, 1, 20),
                CancellationToken.None);

        var card = list.Value.Items.Single();
        detail.Tab.Should().Be(card.Tab);
        detail.DepartureAtUtc.Should().Be(card.DepartureAtUtc);
        detail.StopCount.Should().Be(card.StopCount);
        detail.VisitedCount.Should().Be(card.VisitedCount);
        detail.Status.Should().Be(card.Status);
    }

    // --- read-only (PC-03) ---

    [Fact]
    public async Task Handle_NeverTracksOrChangesAnything()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        await data.AddSessionAsync(itinerary, TravelerId, Departure, Departure.AddHours(3), reachedItems: 1);
        db.ChangeTracker.Clear();

        await Handle(data, TripRecordKinds.CommerceBooking, booking.Id);
        await Handle(data, TripRecordKinds.Itinerary, itinerary.Id);

        db.ChangeTracker.Entries().Should().BeEmpty();
    }
}