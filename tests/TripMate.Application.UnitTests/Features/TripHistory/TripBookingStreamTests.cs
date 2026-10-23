using FluentAssertions;

using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class TripBookingStreamTests
{
    private const long TravelerId = 7;
    private const long OtherTravelerId = 8;

    private static readonly DateTimeOffset Departure = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);

    private static TripStreamFilter Filter(
        string tab = ListTravelerTripsQuery.TabCompleted,
        long travelerId = TravelerId,
        VietnamDayRange range = default) =>
        new(travelerId, tab, range);

    [Fact]
    public async Task Read_ReturnsOnlyTheCallersBookings()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var schedule = await data.AddScheduleAsync(Departure);
        var own = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);
        await data.AddBookingAsync(OtherTravelerId, schedule, BookingStatus.Completed);

        var window = await TripBookingStream.ReadAsync(db, Filter(), 50, CancellationToken.None);

        window.TotalCount.Should().Be(1);
        window.Keys.Should().ContainSingle().Which.Id.Should().Be(own.Id);
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(BookingStatus.Confirmed, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(BookingStatus.Completed, ListTravelerTripsQuery.TabCompleted)]
    [InlineData(BookingStatus.Cancelled, ListTravelerTripsQuery.TabCancelled)]
    [InlineData(BookingStatus.Expired, ListTravelerTripsQuery.TabCancelled)]
    public async Task Read_PlacesEachBookingInExactlyOneTab(BookingStatus status, string expectedTab)
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, status);

        foreach (var tab in ListTravelerTripsQuery.AllowedTabs)
        {
            var window = await TripBookingStream.ReadAsync(db, Filter(tab), 50, CancellationToken.None);

            if (tab == expectedTab)
            {
                window.Keys.Should().ContainSingle().Which.Id.Should().Be(booking.Id);
                window.TotalCount.Should().Be(1);
            }
            else
            {
                window.Keys.Should().BeEmpty();
                window.TotalCount.Should().Be(0);
            }
        }
    }

    [Fact]
    public async Task Read_KeyCarriesTheKindIdAndScheduleStart()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var schedule = await data.AddScheduleAsync(Departure);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);

        var window = await TripBookingStream.ReadAsync(db, Filter(), 50, CancellationToken.None);

        window.Keys.Single().Should().Be(new TripKey(TripRecordKinds.CommerceBooking, booking.Id, Departure));
    }

    [Fact]
    public async Task Read_DateFilterUsesVietnamDayBoundariesOnTheScheduleStart()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var lastMinuteOfOct20 = await data.AddScheduleAsync(new DateTimeOffset(2026, 10, 20, 16, 59, 0, TimeSpan.Zero));
        var firstMinuteOfOct21 = await data.AddScheduleAsync(new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero));
        var lastMinuteOfOct21 = await data.AddScheduleAsync(new DateTimeOffset(2026, 10, 21, 16, 59, 0, TimeSpan.Zero));
        var firstMinuteOfOct22 = await data.AddScheduleAsync(new DateTimeOffset(2026, 10, 21, 17, 0, 0, TimeSpan.Zero));
        await data.AddBookingAsync(TravelerId, lastMinuteOfOct20, BookingStatus.Completed);
        var inFirst = await data.AddBookingAsync(TravelerId, firstMinuteOfOct21, BookingStatus.Completed);
        var inLast = await data.AddBookingAsync(TravelerId, lastMinuteOfOct21, BookingStatus.Completed);
        await data.AddBookingAsync(TravelerId, firstMinuteOfOct22, BookingStatus.Completed);
        VietnamDayRange.TryCreate("2026-10-21", "2026-10-21", out var range).Should().BeTrue();

        var window = await TripBookingStream.ReadAsync(db, Filter(range: range), 50, CancellationToken.None);

        window.TotalCount.Should().Be(2);
        window.Keys.Select(key => key.Id).Should().Equal(inLast.Id, inFirst.Id);
    }

    [Fact]
    public async Task Read_OpenEndedRangeFiltersOnlyTheGivenSide()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var early = await data.AddScheduleAsync(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var late = await data.AddScheduleAsync(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var earlyBooking = await data.AddBookingAsync(TravelerId, early, BookingStatus.Completed);
        var lateBooking = await data.AddBookingAsync(TravelerId, late, BookingStatus.Completed);

        VietnamDayRange.TryCreate("2026-10-01", null, out var fromOnly).Should().BeTrue();
        VietnamDayRange.TryCreate(null, "2026-10-01", out var toOnly).Should().BeTrue();

        (await TripBookingStream.ReadAsync(db, Filter(range: fromOnly), 50, CancellationToken.None))
            .Keys.Select(key => key.Id).Should().Equal(lateBooking.Id);
        (await TripBookingStream.ReadAsync(db, Filter(range: toOnly), 50, CancellationToken.None))
            .Keys.Select(key => key.Id).Should().Equal(earlyBooking.Id);
    }

    [Fact]
    public async Task Read_OrdersByDepartureDescendingThenIdDescending()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        var older = await data.AddScheduleAsync(Departure.AddDays(-5));
        var newer = await data.AddScheduleAsync(Departure);
        var olderBooking = await data.AddBookingAsync(TravelerId, older, BookingStatus.Completed);
        var sameInstantFirst = await data.AddBookingAsync(TravelerId, newer, BookingStatus.Completed);
        var sameInstantSecond = await data.AddBookingAsync(TravelerId, newer, BookingStatus.Completed);

        var window = await TripBookingStream.ReadAsync(db, Filter(), 50, CancellationToken.None);

        window.Keys.Select(key => key.Id).Should().Equal(sameInstantSecond.Id, sameInstantFirst.Id, olderBooking.Id);
    }

    [Fact]
    public async Task Read_TotalCountIsIndependentOfTheWindowSize()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        for (var day = 0; day < 5; day++)
        {
            var schedule = await data.AddScheduleAsync(Departure.AddDays(day));
            await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);
        }

        var window = await TripBookingStream.ReadAsync(db, Filter(), 2, CancellationToken.None);

        window.TotalCount.Should().Be(5);
        window.Keys.Should().HaveCount(2);
        window.Keys[0].DepartureAtUtc.Should().Be(Departure.AddDays(4));
        window.Keys[1].DepartureAtUtc.Should().Be(Departure.AddDays(3));
    }

    [Fact]
    public async Task Read_NoBookings_ReturnsAnEmptyWindow()
    {
        await using var db = TripHistoryTestData.CreateDb();

        var window = await TripBookingStream.ReadAsync(db, Filter(), 50, CancellationToken.None);

        window.TotalCount.Should().Be(0);
        window.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_NonPositiveWindow_Throws()
    {
        await using var db = TripHistoryTestData.CreateDb();

        var act = () => TripBookingStream.ReadAsync(db, Filter(), 0, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Read_UnknownTab_Throws()
    {
        await using var db = TripHistoryTestData.CreateDb();

        var act = () => TripBookingStream.ReadAsync(db, Filter("History"), 10, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}