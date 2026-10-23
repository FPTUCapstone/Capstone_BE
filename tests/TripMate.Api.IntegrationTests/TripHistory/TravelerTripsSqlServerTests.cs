using System.Data.Common;

using FluentAssertions;

using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.Get;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Domain.Entities;

namespace TripMate.Api.IntegrationTests.TripHistory;

/// <summary>
/// The in-memory provider cannot prove that the representative-session, newest-version and
/// booking-count subqueries translate to SQL; these run the real handlers on SQL Server.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class TravelerTripsSqlServerTests
{
    private const long Alice = 1;
    private const long Carol = 4;

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private sealed record Seeded(
        long CompletedBookingId,
        long UpcomingBookingId,
        long CancelledBookingId,
        long LatestSessionItineraryId,
        long ExpiredItineraryId,
        long PlannedItineraryId,
        long CancelledItineraryId,
        long CspNewestActiveId);

    private static async Task<Seeded> SeedAsync(SqlServerTestDatabase database)
    {
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role,status,email,full_name)
            VALUES('Traveler','Active',N'alice@test.invalid',N'Alice'),
                  ('Traveler','Active',N'bob@test.invalid',N'Bob'),
                  ('TourOperator','Active',N'operator@test.invalid',N'Operator'),
                  ('Traveler','Active',N'carol@test.invalid',N'Carol');
            INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,approval_status)
            VALUES(3,N'TM78 Operator',N'TM78-TAX',N'TM78-LICENSE','Approved');
            INSERT commerce.Tours(operator_user_id,title,base_price,duration_days,status)
            VALUES(3,N'Hoi An Lantern Tour',100,1,'Approved');
            INSERT commerce.TourSchedules(tour_id,start_datetime,end_datetime,total_capacity,status)
            VALUES(1,'2026-10-01T02:00:00','2026-10-01T10:00:00',10,'Completed'),
                  (1,'2027-05-01T02:00:00','2027-05-01T10:00:00',10,'Scheduled'),
                  (1,'2026-09-01T02:00:00','2026-09-01T10:00:00',10,'Scheduled');
            -- A booking linked to an itinerary instead of a tour schedule (tour_schedule_id is NULL).
            INSERT planning.Itineraries(traveler_user_id,source_type,title) VALUES(1,'Manual',N'Booking-linked');
            INSERT commerce.Bookings(booking_code,traveler_user_id,tour_schedule_id,itinerary_id,unit_price,total_amount,status,payment_status)
            VALUES('TM78-C1',1,1,NULL,100,3200000,'Completed','Paid'),
                  ('TM78-U1',1,2,NULL,100,3200000,'Confirmed','Paid'),
                  ('TM78-X1',1,3,NULL,100,3200000,'Cancelled','Paid'),
                  ('TM78-B1',2,1,NULL,100,3200000,'Completed','Paid'),
                  ('TM78-L1',1,NULL,1,0,0,'Completed','Paid');
            UPDATE commerce.Bookings SET cancelled_at='2026-08-20T00:00:00', cancel_reason=N'Change of plans'
            WHERE booking_code='TM78-X1';
            INSERT commerce.Tickets(booking_id,ticket_code,status)
            VALUES(1,'T-USED','Used'),(1,'T-VALID','Valid'),(2,'T-UP','Valid');
            INSERT payment.PaymentTransactions(booking_id,gateway,amount,transaction_type,status)
            VALUES(3,'PayOS',800000,'Payment','Success'),(3,'VNPAY',800000,'Payment','Success');
            INSERT payment.Refunds(booking_id,transaction_id,amount,initiated_by,status)
            VALUES(3,1,100000,'System','Failed'),(3,2,800000,'System','Processed');
            -- CSP trips: one request with a regenerated Draft v2 (A1) and one with two Active versions.
            INSERT planning.SchedulingRequests(traveler_user_id,idempotency_key,request_hash,start_at,
                start_latitude,start_longitude,destination_latitude,destination_longitude,available_minutes,search_radius_km)
            VALUES(1,NEWID(),REPLICATE('a',64),'2026-12-01',16,108,16,108,60,2),
                  (1,NEWID(),REPLICATE('b',64),'2026-12-02',16,108,16,108,60,2);
            INSERT planning.Itineraries(traveler_user_id,source_type,title,scheduling_request_id,status,version,valid_from)
            VALUES(1,'CSPGenerated',N'CSP draft pending',1,'Active',1,'2026-12-01'),
                  (1,'CSPGenerated',N'CSP draft pending',1,'Draft',2,'2026-12-01'),
                  (1,'CSPGenerated',N'CSP old',2,'Active',1,'2026-12-02'),
                  (1,'CSPGenerated',N'CSP new',2,'Active',2,'2026-12-02');
            INSERT commerce.Bookings(booking_code,traveler_user_id,tour_schedule_id,unit_price,total_amount,status,payment_status)
            VALUES('TM78-K1',4,2,100,100,'Confirmed','Paid'),('TM78-K2',4,2,100,100,'Confirmed','Paid'),
                  ('TM78-K3',4,2,100,100,'Confirmed','Paid'),('TM78-K4',4,2,100,100,'Confirmed','Paid'),
                  ('TM78-K5',4,2,100,100,'Confirmed','Paid'),('TM78-K6',4,2,100,100,'Confirmed','Paid'),
                  ('TM78-K7',4,2,100,100,'Confirmed','Paid'),('TM78-K8',4,2,100,100,'Confirmed','Paid');
            """);

        await using var context = database.CreateDbContext();
        var seed = new TripHistoryApiSeed(context);
        var latest = await seed.AddItineraryAsync(Alice, visitCount: 3);
        await seed.AddSessionAsync(latest, Alice, new(2026, 9, 10, 1, 0, 0, TimeSpan.Zero), new(2026, 9, 10, 5, 0, 0, TimeSpan.Zero), 3, TimeSpan.FromDays(30));
        await seed.AddSessionAsync(latest, Alice, new(2026, 9, 20, 1, 0, 0, TimeSpan.Zero), new(2026, 9, 20, 5, 0, 0, TimeSpan.Zero), 1, TimeSpan.FromDays(30));
        var expired = await seed.AddItineraryAsync(Alice, visitCount: 2);
        await seed.AddSessionAsync(expired, Alice, new(2026, 8, 2, 2, 0, 0, TimeSpan.Zero), null, 1, TimeSpan.FromHours(2));
        var planned = await seed.AddItineraryAsync(Alice, validFromUtc: new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cancelled = await seed.AddItineraryAsync(Alice, Itinerary.CancelledStatus);

        var newest = await database.ExecuteScalarAsync<long>(
            "SELECT itinerary_id FROM planning.Itineraries WHERE title=N'CSP new'");
        return new Seeded(1, 2, 3, latest.Id, expired.Id, planned.Id, cancelled.Id, newest);
    }

    private static Task<T> RunAsync<T>(
        SqlServerTestDatabase database, Func<ApplicationDbContextAdapter, Task<T>> action) =>
        RunWithInterceptorsAsync(database, [], action);

    private static async Task<T> RunWithInterceptorsAsync<T>(
        SqlServerTestDatabase database,
        IInterceptor[] interceptors,
        Func<ApplicationDbContextAdapter, Task<T>> action)
    {
        await using var context = database.CreateDbContext(interceptors);
        return await action(new ApplicationDbContextAdapter(context));
    }

    private sealed record ApplicationDbContextAdapter(IApplicationDbContext Db)
    {
        public Task<TripMate.Application.Common.Models.Result<TripHistoryPage>> List(
            long travelerId, string tab, string? tripType = null, string? from = null, string? to = null, int page = 1, int pageSize = 20) =>
            new ListTravelerTripsQueryHandler(Db, new FixedClock(Now))
                .Handle(new ListTravelerTripsQuery(travelerId, tab, tripType, from, to, page, pageSize), CancellationToken.None);

        public Task<TripMate.Application.Common.Models.Result<TripDetailResponse>> Get(long travelerId, string kind, long id) =>
            new GetTravelerTripQueryHandler(Db, new FixedClock(Now))
                .Handle(new GetTravelerTripQuery(travelerId, kind, id), CancellationToken.None);
    }

    [SqlServerFact]
    public async Task List_EveryTab_TranslatesAndCountsMatchTheItems()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);

        var completed = await RunAsync(database, api => api.List(Alice, ListTravelerTripsQuery.TabCompleted));
        var upcoming = await RunAsync(database, api => api.List(Alice, ListTravelerTripsQuery.TabUpcoming));
        var cancelled = await RunAsync(database, api => api.List(Alice, ListTravelerTripsQuery.TabCancelled));

        completed.IsSuccess.Should().BeTrue();
        // Latest completed session per itinerary (T1) and an expired-open one (T4); the booking linked to an
        // itinerary has no tour schedule and is not part of the tour-booking stream.
        completed.Value.Items.Select(item => (item.Kind, item.Id)).Should().Equal(
            (TripRecordKinds.CommerceBooking, seed.CompletedBookingId),
            (TripRecordKinds.Itinerary, seed.LatestSessionItineraryId),
            (TripRecordKinds.Itinerary, seed.ExpiredItineraryId));
        completed.Value.TotalCount.Should().Be(completed.Value.Items.Count);
        completed.Value.Summary.Should().Be(new TripHistorySummaryResponse(3, 2));
        var latestCard = completed.Value.Items.Single(item => item.Id == seed.LatestSessionItineraryId);
        latestCard.StopCount.Should().Be(3);
        latestCard.VisitedCount.Should().Be(1);

        upcoming.Value.Items.Select(item => (item.Kind, item.Id)).Should().Equal(
            (TripRecordKinds.CommerceBooking, seed.UpcomingBookingId),
            (TripRecordKinds.Itinerary, seed.PlannedItineraryId),
            (TripRecordKinds.Itinerary, seed.CspNewestActiveId),
            (TripRecordKinds.Itinerary, upcoming.Value.Items.Single(i => i.Title == "CSP draft pending").Id));
        upcoming.Value.TotalCount.Should().Be(upcoming.Value.Items.Count);

        cancelled.Value.Items.Select(item => (item.Kind, item.Id)).Should().Equal(
            (TripRecordKinds.CommerceBooking, seed.CancelledBookingId),
            (TripRecordKinds.Itinerary, seed.CancelledItineraryId));
        cancelled.Value.TotalCount.Should().Be(2);
    }

    [SqlServerFact]
    public async Task List_OnlyTheNewestNonDraftVersionOfACspTripIsShown()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);

        var upcoming = await RunAsync(database, api => api.List(Alice, ListTravelerTripsQuery.TabUpcoming,
            ListTravelerTripsQuery.TripTypeSelfPlannedItinerary));

        var titles = upcoming.Value.Items.Select(item => item.Title).ToArray();
        titles.Should().Contain("CSP new").And.Contain("CSP draft pending");
        titles.Should().NotContain("CSP old");
        upcoming.Value.Items.Should().Contain(item => item.Id == seed.CspNewestActiveId);
    }

    [SqlServerFact]
    public async Task List_PagingAndDateFilterTranslate()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);

        var firstPage = await RunAsync(database, api => api.List(Alice, ListTravelerTripsQuery.TabUpcoming, page: 1, pageSize: 2));
        var secondPage = await RunAsync(database, api => api.List(Alice, ListTravelerTripsQuery.TabUpcoming, page: 2, pageSize: 2));
        var oneDay = await RunAsync(database, api => api.List(Alice, ListTravelerTripsQuery.TabCompleted, from: "2026-10-01", to: "2026-10-01"));

        firstPage.Value.TotalCount.Should().Be(4);
        firstPage.Value.Items.Concat(secondPage.Value.Items).Select(item => (item.Kind, item.Id))
            .Should().OnlyHaveUniqueItems().And.HaveCount(4);
        oneDay.Value.Items.Should().ContainSingle().Which.Kind.Should().Be(TripRecordKinds.CommerceBooking);
    }

    [SqlServerFact]
    public async Task List_TravelersNeverSeeEachOthersRecords()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);

        var bob = await RunAsync(database, api => api.List(2, ListTravelerTripsQuery.TabCompleted));

        bob.Value.Items.Should().ContainSingle().Which.Kind.Should().Be(TripRecordKinds.CommerceBooking);
        bob.Value.Items.Single().Id.Should().NotBe(1);
    }

    [SqlServerFact]
    public async Task Detail_Booking_ReturnsTheLatestRefundAndTheOriginalGateway()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);

        var cancelled = await RunAsync(database, api => api.Get(Alice, TripRecordKinds.CommerceBooking, seed.CancelledBookingId));
        var completed = await RunAsync(database, api => api.Get(Alice, TripRecordKinds.CommerceBooking, seed.CompletedBookingId));

        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Value.Refund.Should().Be(new TripRefundInfo("Processed", 800_000m, "VNPAY"));
        cancelled.Value.Cancellation!.Reason.Should().Be("Change of plans");
        cancelled.Value.Actions.CanViewRefund.Should().BeTrue();
        // T2: the Valid ticket (inserted second, higher id) is preferred over the Used one.
        completed.Value.Actions.TicketId.Should().Be(2);
        completed.Value.Actions.CanReview.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task Detail_Itinerary_UsesTheRepresentativeSessionAndOtherTravelersGetNotFound()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);

        var detail = await RunAsync(database, api => api.Get(Alice, TripRecordKinds.Itinerary, seed.LatestSessionItineraryId));
        var expired = await RunAsync(database, api => api.Get(Alice, TripRecordKinds.Itinerary, seed.ExpiredItineraryId));
        var foreign = await RunAsync(database, api => api.Get(2, TripRecordKinds.Itinerary, seed.LatestSessionItineraryId));

        detail.Value.VisitedCount.Should().Be(1);
        detail.Value.Stops!.Select(stop => stop.VisitStatus).Should().Equal("Reached", "Pending", "Pending");
        expired.Value.Session!.CompletionReason.Should().Be("Expired");
        foreign.ErrorCode.Should().Be(TripHistoryErrorCodes.RecordNotFound);
    }

    [SqlServerFact]
    public async Task Hydration_RunsTheSameNumberOfCommandsForOneCardOrMany()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);

        var one = new CommandCounter();
        var many = new CommandCounter();
        var single = await RunWithInterceptorsAsync(database, [one], api =>
            api.List(Carol, ListTravelerTripsQuery.TabUpcoming, ListTravelerTripsQuery.TripTypeTourBooking, pageSize: 1));
        var eight = await RunWithInterceptorsAsync(database, [many], api =>
            api.List(Carol, ListTravelerTripsQuery.TabUpcoming, ListTravelerTripsQuery.TripTypeTourBooking, pageSize: 8));

        single.Value.Items.Should().HaveCount(1);
        eight.Value.Items.Should().HaveCount(8);
        many.Count.Should().Be(one.Count);
    }

    [SqlServerFact]
    public async Task Reading_WritesNothing()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var counter = new CommandCounter();

        await RunWithInterceptorsAsync(database, [counter], async api =>
        {
            await api.List(Alice, ListTravelerTripsQuery.TabCompleted);
            await api.Get(Alice, TripRecordKinds.Itinerary, seed.ExpiredItineraryId);
            await api.Get(Alice, TripRecordKinds.CommerceBooking, seed.CancelledBookingId);
            return 0;
        });

        counter.NonQueryCount.Should().Be(0);
        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM trip.TripSessions WHERE fsm_state <> 'Completed'"))
            .Should().Be(1, "the lazily expired session must stay open in storage");
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public int NonQueryCount { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            NonQueryCount++;
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            NonQueryCount++;
            return result;
        }
    }
}