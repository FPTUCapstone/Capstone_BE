using FluentAssertions;

using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class TripHistoryKeyReaderTests
{
    private const long TravelerId = 7;

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(60);

    private static ListTravelerTripsQuery Query(
        string tab = ListTravelerTripsQuery.TabUpcoming,
        string? tripType = null,
        string? fromDate = null,
        string? toDate = null,
        int page = 1,
        int pageSize = 20) =>
        new(TravelerId, tab, tripType, fromDate, toDate, page, pageSize);

    private static Task<TripKeyPage> Read(TripHistoryTestData data, ListTravelerTripsQuery query) =>
        TripHistoryKeyReader.ReadPageAsync(data.Db, query, Now, CancellationToken.None);

    private static async Task<TripHistoryTestData> NewDataAsync()
    {
        var data = new TripHistoryTestData(TripHistoryTestData.CreateDb());
        await data.AddTravelerAsync(TravelerId);
        return data;
    }

    private static async Task<(long BookingId, long ItineraryId)> AddOneOfEachAsync(
        TripHistoryTestData data, DateTimeOffset bookingDeparture, DateTimeOffset itineraryDeparture)
    {
        var schedule = await data.AddScheduleAsync(bookingDeparture);
        var booking = await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Confirmed);
        var itinerary = await data.AddItineraryAsync(TravelerId, validFromUtc: itineraryDeparture);
        return (booking.Id, itinerary.Id);
    }

    [Fact]
    public async Task ReadPage_MergesBothStreamsAndSumsTheTotal()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var (bookingId, itineraryId) = await AddOneOfEachAsync(data, Start, Start.AddDays(2));
        var (bookingId2, itineraryId2) = await AddOneOfEachAsync(data, Start.AddDays(4), Start.AddDays(1));

        var page = await Read(data, Query());

        page.TotalCount.Should().Be(4);
        page.Keys.Select(key => (key.Kind, key.Id)).Should().Equal(
            (TripRecordKinds.CommerceBooking, bookingId2),
            (TripRecordKinds.Itinerary, itineraryId),
            (TripRecordKinds.Itinerary, itineraryId2),
            (TripRecordKinds.CommerceBooking, bookingId));
    }

    [Fact]
    public async Task ReadPage_EqualDeparture_BookingBeforeItinerary()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var (bookingId, itineraryId) = await AddOneOfEachAsync(data, Start, Start);

        var page = await Read(data, Query());

        page.Keys.Select(key => key.Kind).Should().Equal(TripRecordKinds.CommerceBooking, TripRecordKinds.Itinerary);
        page.Keys.Select(key => key.Id).Should().Equal(bookingId, itineraryId);
    }

    [Fact]
    public async Task ReadPage_TripTypeLimitsToOneStream()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var (bookingId, itineraryId) = await AddOneOfEachAsync(data, Start, Start.AddDays(1));

        var bookingsOnly = await Read(data, Query(tripType: ListTravelerTripsQuery.TripTypeTourBooking));
        var itinerariesOnly = await Read(data, Query(tripType: ListTravelerTripsQuery.TripTypeSelfPlannedItinerary));
        var all = await Read(data, Query(tripType: ListTravelerTripsQuery.TripTypeAll));
        var omitted = await Read(data, Query(tripType: null));

        bookingsOnly.Keys.Should().ContainSingle().Which.Should().Match<TripKey>(k => k.Id == bookingId && k.Kind == TripRecordKinds.CommerceBooking);
        bookingsOnly.TotalCount.Should().Be(1);
        itinerariesOnly.Keys.Should().ContainSingle().Which.Should().Match<TripKey>(k => k.Id == itineraryId && k.Kind == TripRecordKinds.Itinerary);
        itinerariesOnly.TotalCount.Should().Be(1);
        all.TotalCount.Should().Be(2);
        omitted.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task ReadPage_ConsecutivePagesNeverRepeatOrSkip()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        for (var index = 0; index < 7; index++)
        {
            // Several equal departures on both kinds.
            await AddOneOfEachAsync(data, Start.AddDays(index % 3), Start.AddDays(index % 3));
        }

        var everything = (await Read(data, Query(pageSize: 50))).Keys;
        var paged = new List<TripKey>();
        for (var page = 1; page <= 5; page++)
        {
            paged.AddRange((await Read(data, Query(page: page, pageSize: 3))).Keys);
        }

        everything.Should().HaveCount(14);
        paged.Should().Equal(everything);
        paged.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ReadPage_TotalCountIsTheSameOnEveryPage()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        for (var index = 0; index < 5; index++)
        {
            await AddOneOfEachAsync(data, Start.AddDays(index), Start.AddDays(index));
        }

        (await Read(data, Query(page: 1, pageSize: 4))).TotalCount.Should().Be(10);
        (await Read(data, Query(page: 3, pageSize: 4))).TotalCount.Should().Be(10);
        (await Read(data, Query(page: 3, pageSize: 4))).Keys.Should().HaveCount(2);
        (await Read(data, Query(page: 4, pageSize: 4))).Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadPage_EmptyTab_ReturnsZeroAndNoKeys()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        await AddOneOfEachAsync(data, Start, Start);

        var page = await Read(data, Query(tab: ListTravelerTripsQuery.TabCancelled));

        page.TotalCount.Should().Be(0);
        page.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadPage_AppliesTheVietnamDateRangeToBothStreams()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var inRange = new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero);
        var (bookingId, itineraryId) = await AddOneOfEachAsync(data, inRange, inRange);
        await AddOneOfEachAsync(data, inRange.AddDays(-1), inRange.AddDays(-1));
        await AddOneOfEachAsync(data, inRange.AddDays(1), inRange.AddDays(1));

        var page = await Read(data, Query(fromDate: "2026-10-21", toDate: "2026-10-21"));

        page.TotalCount.Should().Be(2);
        page.Keys.Select(key => key.Id).Should().BeEquivalentTo([bookingId, itineraryId]);
    }

    [Fact]
    public async Task ReadPage_InvalidDates_Throw()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;

        var act = () => Read(data, Query(fromDate: "2026-10-22", toDate: "2026-10-21"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadPage_OnlyOwnRecordsAreReturned()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        await data.AddTravelerAsync(8);
        var schedule = await data.AddScheduleAsync(Start);
        await data.AddBookingAsync(8, schedule, BookingStatus.Confirmed);
        await data.AddItineraryAsync(8);

        (await Read(data, Query())).TotalCount.Should().Be(0);
    }
}