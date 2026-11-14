using System.Data.Common;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TourRecommendations.GetLatest;
using TripMate.Application.Features.TourRecommendations.Refresh;
using TripMate.Domain.Entities;

namespace TripMate.Api.IntegrationTests.TourRecommendations;

public sealed class TourRecommendationSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RefreshAndGet_MapCanonicalTablesAndReturnPersistedScoreWithEmptyReasons()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedRecommendationDataAsync(database);
        await using var db = database.CreateDbContext();
        long travelerId = await db.Users.Where(user => user.Email == "tm71-traveler@example.com")
            .Select(user => user.Id).SingleAsync();
        var refresh = new RefreshTourRecommendationsCommandHandler(
            db,
            new CurrentUser(travelerId),
            new Clock(),
            new TourRecommendationCandidateReader(db));

        var refreshed = await refresh.Handle(
            new RefreshTourRecommendationsCommand(
                Destination: "Da Nang",
                StartDate: new DateOnly(2026, 12, 1),
                EndDate: new DateOnly(2026, 12, 10),
                MinBudget: 1_500_000m,
                MaxBudget: 2_500_000m),
            CancellationToken.None);

        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.Items.Should().ContainSingle();
        refreshed.Value.Items[0].MatchingScore.Should().Be(100m);
        refreshed.Value.Items[0].MatchingReasons.Should().BeEmpty();
        (await db.TourSearchRequests.CountAsync()).Should().Be(1);
        (await db.TourRecommendationLogs.CountAsync()).Should().Be(1);

        db.ChangeTracker.Clear();
        var latest = new GetLatestTourRecommendationsQueryHandler(db, new CurrentUser(travelerId), new Clock());
        var read = await latest.Handle(new GetLatestTourRecommendationsQuery(), CancellationToken.None);

        read.Value.Items.Should().ContainSingle();
        read.Value.Items[0].MatchingScore.Should().Be(100m);
        read.Value.Items[0].MatchingReasons.Should().NotBeNull().And.BeEmpty();
        read.Value.Items[0].Tour.RemainingSlots.Should().Be(8);

        await database.ExecuteNonQueryAsync("""
            UPDATE commerce.Tours
            SET base_price = 9000000
            WHERE title = N'Da Nang Beach';
            UPDATE dbo.TravelerProfiles
            SET interest_tags_json = N'["culture"]'
            WHERE user_id = (SELECT user_id FROM dbo.Users WHERE email = N'tm71-traveler@example.com');
            """);
        db.ChangeTracker.Clear();

        var staleRead = await latest.Handle(new GetLatestTourRecommendationsQuery(), CancellationToken.None);

        staleRead.Value.Items.Should().ContainSingle();
        staleRead.Value.Items[0].MatchingScore.Should().Be(100m,
            "GET must expose the stored snapshot score without recomputing it from current price or interests");
        staleRead.Value.Items[0].Tour.BasePrice.Should().Be(9_000_000);

        var recomputed = await refresh.Handle(
            new RefreshTourRecommendationsCommand(
                Destination: "Da Nang",
                StartDate: new DateOnly(2026, 12, 1),
                EndDate: new DateOnly(2026, 12, 10),
                MinBudget: 1_500_000m,
                MaxBudget: 2_500_000m),
            CancellationToken.None);

        recomputed.Value.Items.Should().BeEmpty(
            "only an explicit refresh may recompute the score from the changed price and interests");
        db.ChangeTracker.Clear();
        (await db.TourRecommendationLogs.OrderBy(log => log.Id).Select(log => log.SimilarityScore).ToListAsync())
            .Should().Equal(100m, 38.89m);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Persistence_ExactThresholdBoundary_Excludes80AndIncludes80Point01()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedRecommendationDataAsync(database);
        await using var db = database.CreateDbContext();
        long travelerId = await db.Users.Where(user => user.Email == "tm71-traveler@example.com")
            .Select(user => user.Id).SingleAsync();
        long tourId = await db.Tours.Select(tour => tour.Id).SingleAsync();
        var snapshot = TourSearchRequest.Create(
            travelerId, null, null, null, null, null, "[\"beach\"]", Now);
        snapshot.AddRecommendation(tourId, 80.00m, 10m, Now);
        snapshot.AddRecommendation(tourId, 80.01m, 10m, Now);
        db.TourSearchRequests.Add(snapshot);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var persisted = await db.TourRecommendationLogs
            .OrderBy(log => log.SimilarityScore)
            .Select(log => new { log.SimilarityScore, log.IsRecommended })
            .ToListAsync();
        persisted.Should().Equal(
            new { SimilarityScore = 80.00m, IsRecommended = false },
            new { SimilarityScore = 80.01m, IsRecommended = true });
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Get_PaginatesTwentyItems_AndExcludesUnpublishedPastAndFullTours()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedRecommendationDataAsync(database);
        await database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT = (
                SELECT user_id FROM dbo.Users WHERE email = N'tm71-traveler@example.com');
            DECLARE @operator BIGINT = (
                SELECT user_id FROM dbo.Users WHERE email = N'tm71-operator@example.com');
            DECLARE @category INT = (SELECT TOP (1) category_id FROM catalog.TourCategories);
            DECLARE @snapshot BIGINT;
            INSERT commerce.TourSearchRequests(traveler_user_id, interest_tags_json, requested_at)
            VALUES (@traveler, N'["beach"]', '2026-11-05T00:00:00');
            SET @snapshot = SCOPE_IDENTITY();

            DECLARE @existing BIGINT = (SELECT TOP (1) tour_id FROM commerce.Tours ORDER BY tour_id);
            INSERT commerce.TourRecommendationLogs(
                search_request_id, tour_id, similarity_score, is_recommended, recommended_at)
            VALUES (@snapshot, @existing, 100.00, 1, '2026-11-05T00:00:00');

            DECLARE @i INT = 1;
            WHILE @i <= 23
            BEGIN
                DECLARE @tour BIGINT;
                DECLARE @status VARCHAR(30) = CASE WHEN @i = 21 THEN 'Draft' ELSE 'Published' END;
                INSERT commerce.Tours(
                    operator_user_id, category_id, title, base_price, duration_days, status, published_at)
                VALUES (
                    @operator, @category, CONCAT(N'Paged Tour ', @i), 2000000, 3, @status,
                    CASE WHEN @status = 'Published' THEN '2026-11-01T00:00:00' ELSE NULL END);
                SET @tour = SCOPE_IDENTITY();
                INSERT commerce.TourSchedules(
                    tour_id, start_datetime, end_datetime, total_capacity, reserved_capacity, status)
                VALUES (
                    @tour,
                    CASE WHEN @i = 22 THEN '2026-11-01T01:00:00' ELSE '2026-12-15T01:00:00' END,
                    CASE WHEN @i = 22 THEN '2026-11-04T01:00:00' ELSE '2026-12-18T01:00:00' END,
                    10,
                    CASE WHEN @i = 23 THEN 10 ELSE 0 END,
                    'Scheduled');
                INSERT commerce.TourRecommendationLogs(
                    search_request_id, tour_id, similarity_score, is_recommended, recommended_at)
                VALUES (@snapshot, @tour, 90.00, 1, '2026-11-05T00:00:00');
                SET @i += 1;
            END;
            """);
        var commandCounter = new CommandCounter();
        await using var db = database.CreateDbContext(commandCounter);
        long travelerId = await db.Users.Where(user => user.Email == "tm71-traveler@example.com")
            .Select(user => user.Id).SingleAsync();
        var latest = new GetLatestTourRecommendationsQueryHandler(db, new CurrentUser(travelerId), new Clock());
        commandCounter.Reset();

        var first = await latest.Handle(new GetLatestTourRecommendationsQuery(Page: 1), CancellationToken.None);
        commandCounter.Count.Should().Be(6);
        commandCounter.Reset();
        var second = await latest.Handle(new GetLatestTourRecommendationsQuery(Page: 2), CancellationToken.None);
        commandCounter.Count.Should().Be(6,
            "page query count must stay bounded rather than grow with returned Tours");

        first.Value.TotalCount.Should().Be(21);
        first.Value.TotalPages.Should().Be(2);
        first.Value.Items.Should().HaveCount(20);
        second.Value.TotalCount.Should().Be(21);
        second.Value.Items.Should().ContainSingle();
        var returnedTitles = first.Value.Items.Concat(second.Value.Items)
            .Select(item => item.Tour.Title)
            .ToArray();
        returnedTitles.Should().NotContain("Paged Tour 21");
        returnedTitles.Should().NotContain("Paged Tour 22");
        returnedTitles.Should().NotContain("Paged Tour 23");

        var hugePage = await latest.Handle(
            new GetLatestTourRecommendationsQuery(Page: int.MaxValue),
            CancellationToken.None);
        hugePage.Value.TotalCount.Should().Be(21);
        hugePage.Value.TotalPages.Should().Be(2);
        hugePage.Value.Items.Should().BeEmpty(
            "a page beyond Int32 SQL OFFSET range is deterministically empty instead of overflowing");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Get_WhenCapacityChangesBetweenQueries_ReturnsOneConsistentSnapshot()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedRecommendationDataAsync(database);
        await using (var seedDb = database.CreateDbContext())
        {
            long travelerId = await seedDb.Users
                .Where(user => user.Email == "tm71-traveler@example.com")
                .Select(user => user.Id)
                .SingleAsync();
            var refresh = new RefreshTourRecommendationsCommandHandler(
                seedDb, new CurrentUser(travelerId), new Clock(), new TourRecommendationCandidateReader(seedDb));
            (await refresh.Handle(new RefreshTourRecommendationsCommand(), CancellationToken.None))
                .Value.Items.Should().ContainSingle();
        }

        var countRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = new BlockingAfterRecommendationCountInterceptor(countRead, releaseRead.Task);
        await using var db = database.CreateDbContext(blocker);
        long currentTravelerId = await db.Users
            .Where(user => user.Email == "tm71-traveler@example.com")
            .Select(user => user.Id)
            .SingleAsync();
        var latest = new GetLatestTourRecommendationsQueryHandler(
            db, new CurrentUser(currentTravelerId), new Clock());

        Task<TripMate.Application.Common.Models.Result<
            TripMate.Application.Features.TourRecommendations.Common.TourRecommendationPageDto>> readTask =
            latest.Handle(new GetLatestTourRecommendationsQuery(), CancellationToken.None);
        await countRead.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task capacityUpdate = database.ExecuteNonQueryAsync("""
            UPDATE commerce.TourSchedules
            SET reserved_capacity = total_capacity
            WHERE status = 'Scheduled';
            """);
        await Task.Delay(250);
        capacityUpdate.IsCompleted.Should().BeFalse(
            "the serializable recommendation read must prevent capacity from changing mid-projection");

        releaseRead.SetResult();
        var read = await readTask;
        await capacityUpdate.WaitAsync(TimeSpan.FromSeconds(15));

        read.Value.TotalCount.Should().Be(1);
        read.Value.Items.Should().ContainSingle();
        db.ChangeTracker.Clear();
        var afterUpdate = await latest.Handle(new GetLatestTourRecommendationsQuery(), CancellationToken.None);
        afterUpdate.Value.TotalCount.Should().Be(0);
        afterUpdate.Value.Items.Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Refresh_WhenCandidateEnumerationFails_RollsBackSnapshotAndLogsTogether()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedTravelersAsync(database);
        await using var db = database.CreateDbContext();
        long travelerId = await db.Users.Where(user => user.Email == "tm71-traveler@example.com")
            .Select(user => user.Id).SingleAsync();
        var handler = new RefreshTourRecommendationsCommandHandler(
            db,
            new CurrentUser(travelerId),
            new Clock(),
            new ThrowingReader());

        Func<Task> action = () => handler.Handle(new RefreshTourRecommendationsCommand(), CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
        await using var verification = database.CreateDbContext();
        (await verification.TourSearchRequests.CountAsync()).Should().Be(0);
        (await verification.TourRecommendationLogs.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRefresh_OlderCommitFinishingLater_DoesNotSupersedeHigherRequestId()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedTravelersAsync(database);
        long travelerId;
        await using (var lookup = database.CreateDbContext())
        {
            travelerId = await lookup.Users.Where(user => user.Email == "tm71-traveler@example.com")
                .Select(user => user.Id).SingleAsync();
        }

        var oldReachedReader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var oldDb = database.CreateDbContext();
        await using var newDb = database.CreateDbContext();
        var oldHandler = new RefreshTourRecommendationsCommandHandler(
            oldDb, new CurrentUser(travelerId), new Clock(), new BlockingEmptyReader(oldReachedReader, releaseOld.Task));
        var newHandler = new RefreshTourRecommendationsCommandHandler(
            newDb, new CurrentUser(travelerId), new Clock(), new EmptyReader());

        Task oldTask = oldHandler.Handle(
            new RefreshTourRecommendationsCommand(Destination: "Old request"), CancellationToken.None);
        await oldReachedReader.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var newer = await newHandler.Handle(
            new RefreshTourRecommendationsCommand(Destination: "New request"), CancellationToken.None);
        newer.IsSuccess.Should().BeTrue();
        releaseOld.SetResult();
        await oldTask;

        await using var verification = database.CreateDbContext();
        var snapshots = await verification.TourSearchRequests
            .Where(request => request.TravelerUserId == travelerId)
            .OrderBy(request => request.Id)
            .Select(request => new { request.Id, request.DestinationText })
            .ToListAsync();
        snapshots.Should().HaveCount(2);
        snapshots[^1].DestinationText.Should().Be("New request");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Get_AfterAvailabilityLoss_RemovesItemAndTotalWithoutLeakingOtherTravelerSnapshot()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedRecommendationDataAsync(database);
        await database.ExecuteNonQueryAsync("""
            DECLARE @other BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'tm71-other@example.com', N'Other Traveler', 'Active');
            SET @other = SCOPE_IDENTITY();
            INSERT dbo.TravelerProfiles(user_id, interest_tags_json)
            VALUES (@other, N'["beach"]');
            INSERT commerce.TourSearchRequests(traveler_user_id, destination_text, interest_tags_json)
            VALUES (@other, N'Other private snapshot', N'["beach"]');
            """);
        await using var db = database.CreateDbContext();
        long travelerId = await db.Users.Where(user => user.Email == "tm71-traveler@example.com")
            .Select(user => user.Id).SingleAsync();
        var refresh = new RefreshTourRecommendationsCommandHandler(
            db, new CurrentUser(travelerId), new Clock(), new TourRecommendationCandidateReader(db));
        (await refresh.Handle(new RefreshTourRecommendationsCommand(), CancellationToken.None))
            .Value.Items.Should().ContainSingle();

        await database.ExecuteNonQueryAsync("""
            UPDATE commerce.TourSchedules
            SET reserved_capacity = total_capacity
            WHERE status = 'Scheduled';
            """);
        db.ChangeTracker.Clear();
        var latest = new GetLatestTourRecommendationsQueryHandler(db, new CurrentUser(travelerId), new Clock());

        var read = await latest.Handle(new GetLatestTourRecommendationsQuery(), CancellationToken.None);

        read.Value.TotalCount.Should().Be(0);
        read.Value.Items.Should().BeEmpty();
        read.Value.Criteria!.Destination.Should().BeNull();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Refresh_WhenTourLosesAvailabilityBeforeResponse_RemovesItemAndTotal()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedRecommendationDataAsync(database);
        await using var db = database.CreateDbContext();
        long travelerId = await db.Users.Where(user => user.Email == "tm71-traveler@example.com")
            .Select(user => user.Id).SingleAsync();
        var reader = new AvailabilityDroppingReader(
            new TourRecommendationCandidateReader(db),
            database);
        var refresh = new RefreshTourRecommendationsCommandHandler(
            db, new CurrentUser(travelerId), new Clock(), reader);

        var result = await refresh.Handle(new RefreshTourRecommendationsCommand(), CancellationToken.None);

        result.Value.TotalCount.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
        (await db.TourRecommendationLogs.CountAsync()).Should().Be(1,
            "the scored candidate remains part of the atomic audit snapshot");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Refresh_ReReadsRepresentativeScheduleAndRemainingSlotsForResponse()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedRecommendationDataAsync(database);
        await using var db = database.CreateDbContext();
        long travelerId = await db.Users.Where(user => user.Email == "tm71-traveler@example.com")
            .Select(user => user.Id).SingleAsync();
        long oldScheduleId = await db.TourSchedules.OrderBy(schedule => schedule.StartAtUtc)
            .Select(schedule => schedule.Id).FirstAsync();
        long fallbackScheduleId = await db.TourSchedules.OrderBy(schedule => schedule.StartAtUtc)
            .Select(schedule => schedule.Id).Skip(1).FirstAsync();
        var reader = new AfterEnumerationReader(
            new TourRecommendationCandidateReader(db),
            () => database.ExecuteNonQueryAsync($"""
                UPDATE commerce.TourSchedules
                SET reserved_capacity = CASE
                    WHEN schedule_id = {oldScheduleId} THEN total_capacity
                    WHEN schedule_id = {fallbackScheduleId} THEN total_capacity - 1
                    ELSE reserved_capacity END
                WHERE schedule_id IN ({oldScheduleId}, {fallbackScheduleId});
                """));
        var refresh = new RefreshTourRecommendationsCommandHandler(
            db, new CurrentUser(travelerId), new Clock(), reader);

        var result = await refresh.Handle(new RefreshTourRecommendationsCommand(), CancellationToken.None);

        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].Tour.RepresentativeScheduleId.Should().Be(fallbackScheduleId.ToString());
        result.Value.Items[0].Tour.RemainingSlots.Should().Be(1,
            "the candidate's earlier value of 8 slots must not leak into the POST response");
        result.Value.Items[0].Tour.AvailabilityStatus.Should().Be("available");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Refresh_UsesFreshClockWhenDepartureAndRegistrationDeadlinePassDuringScoring()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedRecommendationDataAsync(database);
        await database.ExecuteNonQueryAsync($"""
            UPDATE commerce.TourSchedules
            SET start_datetime = '{Now.AddMinutes(2):yyyy-MM-ddTHH:mm:ss}',
                end_datetime = '{Now.AddDays(1):yyyy-MM-ddTHH:mm:ss}',
                registration_deadline_at = '{Now.AddMinutes(1):yyyy-MM-ddTHH:mm:ss}';
            """);
        await using var db = database.CreateDbContext();
        long travelerId = await db.Users.Where(user => user.Email == "tm71-traveler@example.com")
            .Select(user => user.Id).SingleAsync();
        var clock = new MutableClock(Now);
        var reader = new AfterEnumerationReader(
            new TourRecommendationCandidateReader(db),
            () =>
            {
                clock.UtcNow = Now.AddMinutes(3);
                return Task.CompletedTask;
            });
        var refresh = new RefreshTourRecommendationsCommandHandler(
            db, new CurrentUser(travelerId), clock, reader);

        var result = await refresh.Handle(new RefreshTourRecommendationsCommand(), CancellationToken.None);

        result.Value.AsOfUtc.Should().Be(clock.UtcNow.UtcDateTime);
        result.Value.TotalCount.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
        (await db.TourRecommendationLogs.CountAsync()).Should().Be(1,
            "time drift affects final availability, not the complete candidate audit log");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Refresh_LargeCatalogue_StreamsFixedBatchesWithoutCandidateCapOrPerTourQueries()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedRecommendationDataAsync(database);
        await database.ExecuteNonQueryAsync("""
            DECLARE @operator BIGINT = (
                SELECT user_id FROM dbo.Users WHERE email = N'tm71-operator@example.com');
            DECLARE @category INT = (SELECT TOP (1) category_id FROM catalog.TourCategories);
            DECLARE @i INT = 2;
            WHILE @i <= 250
            BEGIN
                DECLARE @tour BIGINT;
                INSERT commerce.Tours(
                    operator_user_id, category_id, title, base_price, duration_days, status, published_at)
                VALUES (
                    @operator, @category, CONCAT(N'Large Catalogue Tour ', @i),
                    2000000, 3, 'Published', '2026-11-01T00:00:00');
                SET @tour = SCOPE_IDENTITY();
                INSERT commerce.TourSchedules(
                    tour_id, start_datetime, end_datetime,
                    total_capacity, reserved_capacity, status)
                VALUES (
                    @tour, '2026-12-15T01:00:00', '2026-12-18T01:00:00',
                    10, 0, 'Scheduled');
                SET @i += 1;
            END;
            """);
        var commandCounter = new CommandCounter();
        await using var db = database.CreateDbContext(commandCounter);
        long travelerId = await db.Users.Where(user => user.Email == "tm71-traveler@example.com")
            .Select(user => user.Id).SingleAsync();
        var observingReader = new ObservingReader(
            new TourRecommendationCandidateReader(db),
            () => db.ChangeTracker.Entries().Count());
        var refresh = new RefreshTourRecommendationsCommandHandler(
            db, new CurrentUser(travelerId), new Clock(), observingReader);
        commandCounter.Reset();

        var result = await refresh.Handle(new RefreshTourRecommendationsCommand(), CancellationToken.None);

        result.Value.TotalCount.Should().Be(250);
        result.Value.Items.Should().HaveCount(20);
        observingReader.BatchSizes.Should().Equal(100, 100, 50);
        observingReader.BatchSizes.Should().OnlyContain(size => size <= 100);
        observingReader.TrackedEntriesAfterPersistedBatch.Should().OnlyContain(count => count == 0,
            "each persisted batch must be detached before candidate enumeration continues");
        (await db.TourRecommendationLogs.CountAsync()).Should().Be(250,
            "streaming must not introduce an arbitrary candidate cap");
        db.ChangeTracker.Entries().Should().BeEmpty(
            "persisted batches and response projections must not retain the whole catalogue");
        commandCounter.Count.Should().Be(13,
            "250 Tours require three candidate batches plus fixed profile, scoring and response projections");
    }

    private static async Task SeedRecommendationDataAsync(SqlServerTestDatabase database)
    {
        await SeedTravelersAsync(database);
        await database.ExecuteNonQueryAsync("""
            DECLARE @operator BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'tm71-operator@example.com', N'TM71 Operator', 'Active');
            SET @operator = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no, approval_status)
            VALUES (@operator, N'TM71 Tours', N'TM71-TAX', N'TM71-LICENSE', 'Approved');

            DECLARE @category INT;
            INSERT catalog.TourCategories(code, name, is_active) VALUES ('BEACH', N'Beach', 1);
            SET @category = SCOPE_IDENTITY();
            DECLARE @destination BIGINT;
            INSERT catalog.Destinations(name) VALUES (N'Da Nang');
            SET @destination = SCOPE_IDENTITY();

            DECLARE @tour BIGINT;
            INSERT commerce.Tours(
                operator_user_id, category_id, title, base_price, duration_days,
                status, published_at)
            VALUES (@operator, @category, N'Da Nang Beach', 2000000, 3,
                'Published', '2026-11-01T00:00:00');
            SET @tour = SCOPE_IDENTITY();
            INSERT commerce.TourDestinations(tour_id, destination_id, sequence_no)
            VALUES (@tour, @destination, 1);
            INSERT commerce.TourSchedules(
                tour_id, start_datetime, end_datetime, total_capacity, reserved_capacity, status)
            VALUES
                (@tour, '2026-11-20T01:00:00', '2026-11-23T01:00:00', 10, 0, 'Scheduled'),
                (@tour, '2026-12-03T01:00:00', '2026-12-06T01:00:00', 10, 2, 'Scheduled');
            """);
    }

    private static Task SeedTravelersAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'tm71-traveler@example.com', N'TM71 Traveler', 'Active');
            SET @traveler = SCOPE_IDENTITY();
            INSERT dbo.TravelerProfiles(user_id, interest_tags_json)
            VALUES (@traveler, N'["beach"]');
            """);

    private sealed record CurrentUser(long Id) : ICurrentUserService
    {
        public long? UserId => Id;
        public string? Role => "Traveler";
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class EmptyReader : ITourRecommendationCandidateReader
    {
        public async IAsyncEnumerable<IReadOnlyList<TourRecommendationCandidate>> ReadBatchesAsync(
            DateTimeOffset nowUtc, DateOnly? startDate, DateOnly? endDate,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class ThrowingReader : ITourRecommendationCandidateReader
    {
        public async IAsyncEnumerable<IReadOnlyList<TourRecommendationCandidate>> ReadBatchesAsync(
            DateTimeOffset nowUtc, DateOnly? startDate, DateOnly? endDate,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("Forced after snapshot insert.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class BlockingEmptyReader(
        TaskCompletionSource reached,
        Task release) : ITourRecommendationCandidateReader
    {
        public async IAsyncEnumerable<IReadOnlyList<TourRecommendationCandidate>> ReadBatchesAsync(
            DateTimeOffset nowUtc, DateOnly? startDate, DateOnly? endDate,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            reached.SetResult();
            await release.WaitAsync(cancellationToken);
            yield break;
        }
    }

    private sealed class AvailabilityDroppingReader(
        ITourRecommendationCandidateReader inner,
        SqlServerTestDatabase database) : ITourRecommendationCandidateReader
    {
        public async IAsyncEnumerable<IReadOnlyList<TourRecommendationCandidate>> ReadBatchesAsync(
            DateTimeOffset nowUtc,
            DateOnly? startDate,
            DateOnly? endDate,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var batch in inner.ReadBatchesAsync(
                nowUtc, startDate, endDate, cancellationToken))
            {
                yield return batch;
                await database.ExecuteNonQueryAsync("""
                    UPDATE commerce.TourSchedules
                    SET reserved_capacity = total_capacity
                    WHERE status = 'Scheduled';
                    """, cancellationToken);
            }
        }
    }

    private sealed class AfterEnumerationReader(
        ITourRecommendationCandidateReader inner,
        Func<Task> afterEnumeration) : ITourRecommendationCandidateReader
    {
        public async IAsyncEnumerable<IReadOnlyList<TourRecommendationCandidate>> ReadBatchesAsync(
            DateTimeOffset nowUtc,
            DateOnly? startDate,
            DateOnly? endDate,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var batch in inner.ReadBatchesAsync(
                nowUtc, startDate, endDate, cancellationToken))
            {
                yield return batch;
            }

            await afterEnumeration();
        }
    }

    private sealed class ObservingReader(
        ITourRecommendationCandidateReader inner,
        Func<int>? trackedEntryCount = null)
        : ITourRecommendationCandidateReader
    {
        public List<int> BatchSizes { get; } = [];

        public List<int> TrackedEntriesAfterPersistedBatch { get; } = [];

        public async IAsyncEnumerable<IReadOnlyList<TourRecommendationCandidate>> ReadBatchesAsync(
            DateTimeOffset nowUtc,
            DateOnly? startDate,
            DateOnly? endDate,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var batch in inner.ReadBatchesAsync(
                nowUtc, startDate, endDate, cancellationToken))
            {
                BatchSizes.Add(batch.Count);
                yield return batch;
                if (trackedEntryCount is not null)
                {
                    TrackedEntriesAfterPersistedBatch.Add(trackedEntryCount());
                }
            }
        }
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public void Reset() => Count = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Count++;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingAfterRecommendationCountInterceptor(
        TaskCompletionSource countRead,
        Task release) : DbCommandInterceptor
    {
        private int _blocked;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT_BIG", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("TourRecommendationLogs", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                countRead.SetResult();
                await release.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}