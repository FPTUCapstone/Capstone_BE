using FluentAssertions;

using TripMate.Application.Features.TripHistory.Common;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class TripKeyMergerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static TripKey Booking(long id, int day) => new(TripRecordKinds.CommerceBooking, id, T0.AddDays(day));

    private static TripKey Itinerary(long id, int day) => new(TripRecordKinds.Itinerary, id, T0.AddDays(day));

    [Fact]
    public void Compare_OrdersByDepartureDescending()
    {
        TripKeyMerger.Order.Compare(Booking(1, 5), Itinerary(1, 3)).Should().BeNegative();
        TripKeyMerger.Order.Compare(Itinerary(1, 3), Booking(1, 5)).Should().BePositive();
    }

    [Fact]
    public void Compare_EqualDeparture_BookingsComeBeforeItineraries()
    {
        TripKeyMerger.Order.Compare(Booking(1, 5), Itinerary(99, 5)).Should().BeNegative();
        TripKeyMerger.Order.Compare(Itinerary(99, 5), Booking(1, 5)).Should().BePositive();
    }

    [Fact]
    public void Compare_EqualDepartureAndKind_HigherIdComesFirst()
    {
        TripKeyMerger.Order.Compare(Booking(9, 5), Booking(2, 5)).Should().BeNegative();
        TripKeyMerger.Order.Compare(Itinerary(2, 5), Itinerary(9, 5)).Should().BePositive();
    }

    [Fact]
    public void Compare_IdenticalKeys_AreEqual()
    {
        TripKeyMerger.Order.Compare(Booking(3, 5), Booking(3, 5)).Should().Be(0);
    }

    [Fact]
    public void Merge_InterleavesTwoSortedStreamsInTheTotalOrder()
    {
        TripKey[] bookings = [Booking(5, 9), Booking(4, 5), Booking(1, 1)];
        TripKey[] itineraries = [Itinerary(7, 9), Itinerary(6, 6), Itinerary(2, 1)];

        var page = TripKeyMerger.Merge(bookings, itineraries, page: 1, pageSize: 10);

        page.Should().Equal(
            Booking(5, 9), Itinerary(7, 9), Itinerary(6, 6), Booking(4, 5), Booking(1, 1), Itinerary(2, 1));
    }

    [Fact]
    public void Merge_SlicesTheRequestedPage()
    {
        TripKey[] bookings = [Booking(5, 9), Booking(4, 5), Booking(1, 1)];
        TripKey[] itineraries = [Itinerary(7, 9), Itinerary(6, 6), Itinerary(2, 1)];

        TripKeyMerger.Merge(bookings, itineraries, page: 1, pageSize: 4)
            .Should().Equal(Booking(5, 9), Itinerary(7, 9), Itinerary(6, 6), Booking(4, 5));
        TripKeyMerger.Merge(bookings, itineraries, page: 2, pageSize: 4)
            .Should().Equal(Booking(1, 1), Itinerary(2, 1));
        TripKeyMerger.Merge(bookings, itineraries, page: 3, pageSize: 4).Should().BeEmpty();
    }

    [Fact]
    public void Merge_ConsecutivePagesNeverRepeatOrSkipAcrossKindsAndEqualDates()
    {
        // Many equal departures on both kinds: the worst case for unstable paging.
        var bookings = Enumerable.Range(1, 7).Select(id => Booking(id, id % 3)).OrderBy(k => k, TripKeyMerger.Order).ToArray();
        var itineraries = Enumerable.Range(1, 6).Select(id => Itinerary(id, id % 3)).OrderBy(k => k, TripKeyMerger.Order).ToArray();
        var everything = bookings.Concat(itineraries).OrderBy(k => k, TripKeyMerger.Order).ToArray();

        var paged = Enumerable.Range(1, 5)
            .SelectMany(page => TripKeyMerger.Merge(
                bookings.Take(page * 3).ToArray(),
                itineraries.Take(page * 3).ToArray(),
                page,
                pageSize: 3))
            .ToArray();

        paged.Should().Equal(everything);
        paged.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Merge_OneStreamEmpty_ReturnsTheOtherPaged()
    {
        TripKey[] bookings = [Booking(5, 9), Booking(4, 5), Booking(1, 1)];

        TripKeyMerger.Merge(bookings, [], page: 2, pageSize: 2).Should().Equal(Booking(1, 1));
        TripKeyMerger.Merge([], bookings, page: 1, pageSize: 2).Should().Equal(Booking(5, 9), Booking(4, 5));
    }

    [Fact]
    public void Merge_BothEmpty_ReturnsNothing()
    {
        TripKeyMerger.Merge([], [], page: 1, pageSize: 20).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(-1, 10)]
    public void Merge_InvalidPaging_Throws(int page, int pageSize)
    {
        var act = () => TripKeyMerger.Merge([], [], page, pageSize);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}