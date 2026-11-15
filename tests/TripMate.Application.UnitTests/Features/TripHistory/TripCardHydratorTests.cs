using FluentAssertions;

using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class TripCardHydratorTests
{
    private const long TravelerId = 7;
    private const long OtherTravelerId = 8;

    private static readonly DateTimeOffset Departure = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Departure.AddDays(90);

    private static async Task<TripHistoryTestData> NewDataAsync()
    {
        var data = new TripHistoryTestData(TripHistoryTestData.CreateDb());
        await data.AddTravelerAsync(TravelerId);
        await data.AddTravelerAsync(OtherTravelerId);
        return data;
    }

    private static TripKey BookingKey(Booking booking, DateTimeOffset? departure = null) =>
        new(TripRecordKinds.CommerceBooking, booking.Id, departure ?? Departure);

    private static TripKey ItineraryKey(Itinerary itinerary, DateTimeOffset? departure = null) =>
        new(TripRecordKinds.Itinerary, itinerary.Id, departure ?? Departure);

    private static async Task<TripHistoryItemResponse> HydrateOne(
        TripHistoryTestData data, string tab, TripKey key, long travelerId = TravelerId) =>
        (await TripCardHydrator.HydrateAsync(data.Db, travelerId, tab, [key], Now, CancellationToken.None)).Single();

    // --- booking cards ---

    [Fact]
    public async Task Booking_MapsTheCardFields()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure, tourTitle: "Hoi An Lantern Tour");
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed, quantity: 3, totalAmount: 4_500_000m);

        var card = await HydrateOne(data, ListTravelerTripsQuery.TabCompleted, BookingKey(booking));

        card.Kind.Should().Be(TripRecordKinds.CommerceBooking);
        card.Id.Should().Be(booking.Id);
        card.TripType.Should().Be(ListTravelerTripsQuery.TripTypeTourBooking);
        card.Tab.Should().Be(ListTravelerTripsQuery.TabCompleted);
        card.Title.Should().Be("Hoi An Lantern Tour");
        card.DepartureAtUtc.Should().Be(Departure);
        card.Status.Should().Be(nameof(BookingStatus.Completed));
        card.BookingCode.Should().Be($"TM-{booking.Id:D6}");
        card.ParticipantCount.Should().Be(3);
        card.TotalAmount.Should().Be(4_500_000m);
        card.StopCount.Should().BeNull();
        card.VisitedCount.Should().BeNull();
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(BookingStatus.Confirmed, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(BookingStatus.Completed, ListTravelerTripsQuery.TabCompleted)]
    [InlineData(BookingStatus.Cancelled, ListTravelerTripsQuery.TabCancelled)]
    [InlineData(BookingStatus.Expired, ListTravelerTripsQuery.TabCancelled)]
    public async Task Booking_AlwaysCarriesTheTypedReviewableRecord(BookingStatus status, string tab)
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, status);

        var card = await HydrateOne(data, tab, BookingKey(booking));

        card.ReviewableRecord.Should().NotBeNull();
        card.ReviewableRecord!.Kind.Should().Be("commerceBooking");
        card.ReviewableRecord.Id.Should().Be(booking.Id);
    }

    [Fact]
    public async Task CanReview_CompletedAndNotYetReviewed_IsTrue()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);

        var card = await HydrateOne(data, ListTravelerTripsQuery.TabCompleted, BookingKey(booking));

        card.Actions.CanReview.Should().BeTrue();
        card.Review.Should().Be(new TripReviewStateResponse(false, null, null));
    }

    [Fact]
    public async Task CanReview_CompletedAndAlreadyReviewed_IsFalseWithTheReviewState()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);
        await data.AddReviewAsync(booking.Id, TravelerId, id: 41, rating: 4);

        var card = await HydrateOne(data, ListTravelerTripsQuery.TabCompleted, BookingKey(booking));

        card.Actions.CanReview.Should().BeFalse();
        card.Review.Should().Be(new TripReviewStateResponse(true, 41, 4));
    }

    [Fact]
    public async Task Review_SeveralRowsForOneBooking_ReportsTheLowestId()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);
        await data.AddReviewAsync(booking.Id, TravelerId, id: 60, rating: 2);
        await data.AddReviewAsync(booking.Id, TravelerId, id: 15, rating: 5);

        var card = await HydrateOne(data, ListTravelerTripsQuery.TabCompleted, BookingKey(booking));

        card.Review.Should().Be(new TripReviewStateResponse(true, 15, 5));
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(BookingStatus.Confirmed, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(BookingStatus.Cancelled, ListTravelerTripsQuery.TabCancelled)]
    [InlineData(BookingStatus.Expired, ListTravelerTripsQuery.TabCancelled)]
    public async Task CanReview_NotCompleted_IsFalse(BookingStatus status, string tab)
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, status);

        (await HydrateOne(data, tab, BookingKey(booking))).Actions.CanReview.Should().BeFalse();
    }

    // --- e-ticket (P7, T2) ---

    [Fact]
    public async Task Ticket_ValidIsPreferredOverUsedRegardlessOfId()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);
        await data.AddTicketAsync(booking.Id, TicketStatus.Used, id: 3);
        await data.AddTicketAsync(booking.Id, TicketStatus.Valid, id: 9);

        var card = await HydrateOne(data, ListTravelerTripsQuery.TabCompleted, BookingKey(booking));

        card.Actions.CanViewETicket.Should().BeTrue();
        card.Actions.TicketId.Should().Be(9);
    }

    [Fact]
    public async Task Ticket_AmongSeveralValid_TheLowestIdWins()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Confirmed);
        await data.AddTicketAsync(booking.Id, TicketStatus.Valid, id: 8);
        await data.AddTicketAsync(booking.Id, TicketStatus.Valid, id: 5);

        (await HydrateOne(data, ListTravelerTripsQuery.TabUpcoming, BookingKey(booking))).Actions.TicketId.Should().Be(5);
    }

    [Fact]
    public async Task Ticket_AmongSeveralUsed_TheLowestIdWins()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);
        await data.AddTicketAsync(booking.Id, TicketStatus.Used, id: 4);
        await data.AddTicketAsync(booking.Id, TicketStatus.Used, id: 2);

        (await HydrateOne(data, ListTravelerTripsQuery.TabCompleted, BookingKey(booking))).Actions.TicketId.Should().Be(2);
    }

    [Fact]
    public async Task Ticket_CancelledAndExpiredAreNeverEligible()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Confirmed);
        await data.AddTicketAsync(booking.Id, TicketStatus.Cancelled, id: 1);
        await data.AddTicketAsync(booking.Id, TicketStatus.Expired, id: 2);

        var card = await HydrateOne(data, ListTravelerTripsQuery.TabUpcoming, BookingKey(booking));

        card.Actions.CanViewETicket.Should().BeFalse();
        card.Actions.TicketId.Should().BeNull();
    }

    [Fact]
    public async Task Ticket_IgnoresTicketsOfOtherBookings()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var withTicket = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Confirmed);
        var without = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Confirmed);
        await data.AddTicketAsync(withTicket.Id, TicketStatus.Valid, id: 6);

        var cards = await TripCardHydrator.HydrateAsync(
            db, TravelerId, ListTravelerTripsQuery.TabUpcoming, [BookingKey(withTicket), BookingKey(without)], Now, CancellationToken.None);

        cards[0].Actions.TicketId.Should().Be(6);
        cards[1].Actions.TicketId.Should().BeNull();
        cards[1].Actions.CanViewETicket.Should().BeFalse();
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(BookingStatus.Cancelled, ListTravelerTripsQuery.TabCancelled)]
    [InlineData(BookingStatus.Expired, ListTravelerTripsQuery.TabCancelled)]
    public async Task ETicket_OnlyForConfirmedOrCompletedBookings(BookingStatus status, string tab)
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, status);
        await data.AddTicketAsync(booking.Id, TicketStatus.Valid, id: 1);

        var card = await HydrateOne(data, tab, BookingKey(booking));

        card.Actions.CanViewETicket.Should().BeFalse();
        card.Actions.TicketId.Should().BeNull();
    }

    // --- refund (P8) ---

    [Fact]
    public async Task Refund_CancelledBookingWithARefund_CanViewRefund()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Cancelled);
        await data.AddRefundAsync(booking.Id, id: 1);
        await data.AddRefundAsync(booking.Id, id: 2);

        (await HydrateOne(data, ListTravelerTripsQuery.TabCancelled, BookingKey(booking)))
            .Actions.CanViewRefund.Should().BeTrue();
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled, false, ListTravelerTripsQuery.TabCancelled)]
    [InlineData(BookingStatus.Expired, true, ListTravelerTripsQuery.TabCancelled)]
    [InlineData(BookingStatus.Completed, true, ListTravelerTripsQuery.TabCompleted)]
    [InlineData(BookingStatus.Confirmed, true, ListTravelerTripsQuery.TabUpcoming)]
    public async Task Refund_OtherwiseCannotViewRefund(BookingStatus status, bool hasRefund, string tab)
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, status);
        if (hasRefund)
        {
            await data.AddRefundAsync(booking.Id, id: 1);
        }

        (await HydrateOne(data, tab, BookingKey(booking))).Actions.CanViewRefund.Should().BeFalse();
    }

    // --- itinerary cards ---

    [Fact]
    public async Task Itinerary_Completed_CountsStopsAndVisitsFromTheRepresentativeSession()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, visitCount: 4);
        await data.AddSessionAsync(itinerary, TravelerId, Departure, Departure.AddHours(2), reachedItems: 1);
        await data.AddSessionAsync(itinerary, TravelerId, Departure.AddDays(1), Departure.AddDays(1).AddHours(2), reachedItems: 3);

        var card = await HydrateOne(data, ListTravelerTripsQuery.TabCompleted, ItineraryKey(itinerary, Departure.AddDays(1)));

        card.Kind.Should().Be(TripRecordKinds.Itinerary);
        card.Id.Should().Be(itinerary.Id);
        card.TripType.Should().Be(ListTravelerTripsQuery.TripTypeSelfPlannedItinerary);
        card.Tab.Should().Be(ListTravelerTripsQuery.TabCompleted);
        card.Title.Should().Be("Trip");
        card.DepartureAtUtc.Should().Be(Departure.AddDays(1));
        card.Status.Should().Be("Completed");
        card.StopCount.Should().Be(4);
        card.VisitedCount.Should().Be(3);
        card.BookingCode.Should().BeNull();
        card.TotalAmount.Should().BeNull();
    }

    [Fact]
    public async Task Itinerary_CompletedByExpiry_CountsTheVisitsMadeBeforeItExpired()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, visitCount: 3);
        await data.AddSessionAsync(itinerary, TravelerId, Departure, reachedItems: 2, lifetime: TimeSpan.FromDays(2));

        var card = await HydrateOne(data, ListTravelerTripsQuery.TabCompleted, ItineraryKey(itinerary));

        card.Status.Should().Be("Completed");
        card.StopCount.Should().Be(3);
        card.VisitedCount.Should().Be(2);
    }

    [Theory]
    [InlineData(Itinerary.ActiveStatus, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(Itinerary.CancelledStatus, ListTravelerTripsQuery.TabCancelled)]
    public async Task Itinerary_NotCompleted_CountsPlannedStopsAndHasNoVisitCount(string status, string tab)
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, status, visitCount: 5);

        var card = await HydrateOne(data, tab, ItineraryKey(itinerary));

        card.Status.Should().Be(status);
        card.StopCount.Should().Be(5);
        card.VisitedCount.Should().BeNull();
    }

    [Fact]
    public async Task Itinerary_HasNoReviewableRecordAndNoActions()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId);
        await data.AddSessionAsync(itinerary, TravelerId, Departure, Departure.AddHours(2));

        var card = await HydrateOne(data, ListTravelerTripsQuery.TabCompleted, ItineraryKey(itinerary));

        card.ReviewableRecord.Should().BeNull();
        card.Review.Should().BeNull();
        card.Actions.Should().Be(new TripActionsResponse(false, false, null, false));
    }

    // --- shared behavior ---

    [Fact]
    public async Task Hydrate_PreservesTheKeyOrderAcrossKinds()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Confirmed);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var secondBooking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Confirmed);

        var cards = await TripCardHydrator.HydrateAsync(
            db,
            TravelerId,
            ListTravelerTripsQuery.TabUpcoming,
            [ItineraryKey(itinerary), BookingKey(secondBooking), BookingKey(booking)],
            Now,
            CancellationToken.None);

        cards.Select(card => (card.Kind, card.Id)).Should().Equal(
            (TripRecordKinds.Itinerary, itinerary.Id),
            (TripRecordKinds.CommerceBooking, secondBooking.Id),
            (TripRecordKinds.CommerceBooking, booking.Id));
    }

    [Fact]
    public async Task Hydrate_DropsRecordsThatDoNotBelongToTheCaller()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(Departure);
        var foreignBooking = await data.AddBookingAsync(OtherTravelerId, schedule, BookingStatus.Confirmed);
        var foreignItinerary = await data.AddItineraryAsync(OtherTravelerId);

        var cards = await TripCardHydrator.HydrateAsync(
            db,
            TravelerId,
            ListTravelerTripsQuery.TabUpcoming,
            [BookingKey(foreignBooking), ItineraryKey(foreignItinerary)],
            Now,
            CancellationToken.None);

        cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Hydrate_NoKeys_ReturnsNothing()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;

        (await TripCardHydrator.HydrateAsync(db, TravelerId, ListTravelerTripsQuery.TabUpcoming, [], Now, CancellationToken.None))
            .Should().BeEmpty();
    }
}