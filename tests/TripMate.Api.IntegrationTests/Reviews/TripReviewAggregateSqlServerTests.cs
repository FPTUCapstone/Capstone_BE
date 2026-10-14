using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Detail;
using TripMate.Application.Features.PointsOfInterest.Explore;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class TripReviewAggregateSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task Tour_Empty_ReturnsNullAndZero()
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();

        var result = await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(TripReviewAggregateDto.Empty);
    }

    [SqlServerTheory]
    [InlineData(false, true, 3, 1)]
    [InlineData(true, false, 5, 1)]
    [InlineData(true, true, 4, 2)]
    public async Task Tour_CombinesCanonicalAndLegacyWithoutInventingMirrorRows(
        bool canonical, bool legacy, decimal expectedAverage, int expectedCount)
    {
        await using var database = await SeedAsync();
        if (canonical) await AddCanonicalAsync(database, 1, 5);
        if (legacy) await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(1,'Tour',1,2,3);
            """);
        await using var context = database.CreateDbContext();

        var result = await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new TripReviewAggregateDto(expectedAverage, expectedCount));
        if (canonical && !legacy)
            (await context.Reviews.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    public async Task Tour_PreservesLegitimateRowsAndExcludesOtherSubjects()
    {
        await using var database = await SeedAsync();
        await AddCanonicalAsync(database, 1, 5);
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(1,'Tour',1,2,1),(1,'Tour',1,NULL,3),(1,'POI',1,NULL,1),(1,'Tour',99,NULL,1);
            """);
        await using var context = database.CreateDbContext();

        var result = await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance);

        result.Value.Should().Be(new TripReviewAggregateDto(3.0m, 3));
    }

    [SqlServerFact]
    public async Task Tour_SameBookingCanonicalAndLegacy_CanonicalWinsWithoutMutation()
    {
        await using var database = await SeedAsync();
        await AddCanonicalAsync(database, 1, 5);
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(1,'Tour',1,1,5);
            """);
        await using var context = database.CreateDbContext();

        var logger = new RecordingLogger();
        var result = await TripReviewAggregateReader.ReadTourAsync(context, 1, logger);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new TripReviewAggregateDto(5m, 1));
        (await TripReviewAggregateReader.QueryConflictingLegacyTourTargetIds(context)
                .AnyAsync(targetId => targetId == 1))
            .Should().BeTrue("the excluded overlap must remain observable as a data-integrity event");
        (await context.TripReviews.CountAsync()).Should().Be(1);
        (await context.Reviews.CountAsync()).Should().Be(1);
        logger.Messages.Should().ContainSingle(message =>
            message.Contains("canonical review won over legacy Tour review", StringComparison.Ordinal));
    }

    [SqlServerTheory]
    [InlineData("POI", 10)]
    [InlineData("Tour", 99)]
    [InlineData("RouteSegment", 10)]
    public async Task Tour_SameBookingAnyLegacyTarget_CanonicalWins(
        string targetType, long targetId)
    {
        await using var database = await SeedAsync();
        await AddCanonicalAsync(database, 1, 5);
        await database.ExecuteNonQueryAsync($"""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(1,'{targetType}',{targetId},1,4);
            """);
        await using var context = database.CreateDbContext();

        var result = await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new TripReviewAggregateDto(5m, 1));
        (await context.TripReviews.CountAsync()).Should().Be(1);
        (await context.Reviews.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    public async Task Tour_MultipleCanonicalBookings_AreAllPreserved()
    {
        await using var database = await SeedAsync();
        await AddCanonicalAsync(database, 1, 5);
        await AddCanonicalAsync(database, 2, 3);
        await using var context = database.CreateDbContext();

        var result = await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance);

        result.Value.Should().Be(new TripReviewAggregateDto(4m, 2));
    }

    [SqlServerFact]
    public async Task Tour_EqualValuedLegitimateRows_AreNotCollapsed()
    {
        await using var database = await SeedAsync();
        await AddCanonicalAsync(database, 1, 4);
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(1,'Tour',1,2,4),(1,'Tour',1,NULL,4);
            """);
        await using var context = database.CreateDbContext();

        (await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance)).Value
            .Should().Be(new TripReviewAggregateDto(4m, 3));
    }

    [SqlServerFact]
    public async Task Tour_ItineraryOnlyCanonical_IsExcluded()
    {
        await using var database = await SeedAsync();
        await using (var setup = database.CreateDbContext())
        {
            setup.TripReviews.Add(TripReview.CreatePublished(1, 1, null, 1, 5,
                "Itinerary title", "Itinerary content", null, null, false,
                "Aggregate Owner", null, Now));
            await setup.SaveChangesAsync();
        }
        await using var context = database.CreateDbContext();

        var result = await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance);

        result.Value.Should().Be(TripReviewAggregateDto.Empty);
    }

    [SqlServerFact]
    public async Task Tour_LegacyContributorSharingBookingWithItineraryParent_IsExcluded()
    {
        await using var database = await SeedAsync();
        await using (var setup = database.CreateDbContext())
        {
            setup.TripReviews.Add(TripReview.CreatePublished(1, 1, null, 1, 5,
                "Itinerary title", "Itinerary content", null, null, false,
                "Aggregate Owner", null, Now));
            await setup.SaveChangesAsync();
        }
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(1,'Tour',1,1,4);
            """);
        await using var context = database.CreateDbContext();

        var result = await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(TripReviewAggregateDto.Empty);
    }

    [SqlServerFact]
    public async Task Tour_C4Fields_DoNotChangeOverallAggregate()
    {
        await using var database = await SeedAsync();
        await using (var setup = database.CreateDbContext())
        {
            setup.TripReviews.Add(TripReview.CreatePublished(1, 1, 1, null, 4,
                "C4 title", "C4 content", RoutePacingFeedback.TooTight, 1, false,
                "Aggregate Owner", null, Now));
            await setup.SaveChangesAsync();
        }
        await using var context = database.CreateDbContext();

        (await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance)).Value
            .Should().Be(new TripReviewAggregateDto(4m, 1));
    }

    [SqlServerFact]
    public async Task Tour_EditChangesAverageWithoutChangingCount()
    {
        await using var database = await SeedAsync();
        await AddCanonicalAsync(database, 1, 2);
        await using (var edit = database.CreateDbContext())
        {
            var review = await edit.TripReviews.SingleAsync();
            review.TryEditPublished(5, "Edited title", "Edited content", false,
                "Aggregate Owner", null, Now.AddHours(1)).Should().BeTrue();
            await edit.SaveChangesAsync();
        }
        await using var context = database.CreateDbContext();

        (await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance)).Value
            .Should().Be(new TripReviewAggregateDto(5m, 1));
    }

    [SqlServerFact]
    public async Task Tour_RolledBackEditLeavesAggregateUnchanged()
    {
        await using var database = await SeedAsync();
        await AddCanonicalAsync(database, 1, 2);
        await using (var edit = database.CreateDbContext())
        await using (var transaction = await edit.Database.BeginTransactionAsync())
        {
            var review = await edit.TripReviews.SingleAsync();
            review.TryEditPublished(5, "Edited title", "Edited content", false,
                "Aggregate Owner", null, Now.AddHours(1)).Should().BeTrue();
            await edit.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using var context = database.CreateDbContext();

        (await TripReviewAggregateReader.ReadTourAsync(context, 1, NullLogger.Instance)).Value
            .Should().Be(new TripReviewAggregateDto(2m, 1));
    }

    [SqlServerFact]
    public async Task Poi_EmptyAndSingleLegacyHaveCanonicalNullAndCountSemantics()
    {
        await using var database = await SeedAsync();
        await using (var empty = database.CreateDbContext())
            (await TripReviewAggregateReader.QueryPoiReviews(empty).AnyAsync()).Should().BeFalse();
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(1,'POI',10,NULL,3);
            """);
        await using var context = database.CreateDbContext();
        var ratings = TripReviewAggregateReader.QueryPoiReviews(context).Where(x => x.TargetId == 10);

        (await ratings.CountAsync()).Should().Be(1);
        TripReviewAggregateDto.Round(await ratings.AverageAsync(x => (decimal?)x.Rating)).Should().Be(3m);
    }

    [SqlServerFact]
    public async Task Poi_SameBookingCanonicalAndLegacy_CanonicalWinsInDetailAndExplore()
    {
        await using var database = await SeedAsync();
        await AddCanonicalAsync(database, 1, 5);
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(1,'POI',1,1,4);
            """);
        await using var detailContext = database.CreateDbContext();

        var detailLogger = new RecordingLogger<GetPoiDetailQueryHandler>();
        var detail = await new GetPoiDetailQueryHandler(
            detailContext,
            new Clock(Now),
            detailLogger)
            .Handle(new GetPoiDetailQuery(1), default);

        detail.IsSuccess.Should().BeTrue();
        detail.Value.AverageRating.Should().BeNull();
        detail.Value.ReviewCount.Should().Be(0);
        (await TripReviewAggregateReader.QueryConflictingLegacyPoiTargetIds(detailContext)
                .AnyAsync(targetId => targetId == 1))
            .Should().BeTrue("the excluded overlap must remain observable as a data-integrity event");
        await using var exploreContext = database.CreateDbContext();
        var exploreLogger = new RecordingLogger<ExplorePoisQueryHandler>();
        var explore = await new ExplorePoisQueryHandler(
            exploreContext,
            new Clock(Now),
            exploreLogger)
            .Handle(new ExplorePoisQuery(), default);
        explore.IsSuccess.Should().BeTrue();
        var item = explore.Value.Items.Should().ContainSingle().Subject;
        item.AverageRating.Should().BeNull();
        item.ReviewCount.Should().Be(0);
        detailLogger.Messages.Should().ContainSingle(message =>
            message.Contains("canonical review won over legacy POI review", StringComparison.Ordinal));
        exploreLogger.Messages.Should().ContainSingle(message =>
            message.Contains("canonical reviews won over legacy POI reviews", StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task Poi_LegacyRowsRemainIncludedAndRoundAwayFromZero()
    {
        await using var database = await SeedAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(1,'POI',10,NULL,4),(1,'POI',10,NULL,4),
                  (1,'POI',10,NULL,4),(1,'POI',10,NULL,5),
                  (1,'Tour',10,NULL,1);
            """);
        await using var context = database.CreateDbContext();

        var aggregate = await TripReviewAggregateReader.QueryPoiReviews(context)
            .Where(item => item.TargetId == 10)
            .GroupBy(_ => 1)
            .Select(group => new { Average = group.Average(x => (decimal?)x.Rating), Count = group.Count() })
            .SingleAsync();

        aggregate.Count.Should().Be(4);
        TripReviewAggregateDto.Round(aggregate.Average).Should().Be(4.3m);
        (await TripReviewAggregateReader.QueryPoiReviews(context).AnyAsync(x => x.TargetId == 11))
            .Should().BeFalse();
    }

    [SqlServerTheory]
    [InlineData(false, true, 3, 1)]
    [InlineData(true, false, 5, 1)]
    [InlineData(true, true, 4, 2)]
    public async Task Poi_CombinesCanonicalServiceAndLegacyWithoutCrossNamespaceCollision(
        bool canonical, bool legacy, decimal expectedAverage, int expectedCount)
    {
        await using var database = await SeedAsync();
        if (canonical) await AddCanonicalPoiAsync(database, 1, 5);
        if (legacy) await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating)
            VALUES(1,'POI',1,1,3);
            """);
        await using var context = database.CreateDbContext();

        var ratings = TripReviewAggregateReader.QueryPoiReviews(context)
            .Where(item => item.TargetId == 1);
        var aggregate = await ratings.GroupBy(_ => 1)
            .Select(group => new
            {
                Average = group.Average(item => (decimal?)item.Rating),
                Count = group.Count(),
            })
            .FirstOrDefaultAsync();

        aggregate.Should().NotBeNull();
        TripReviewAggregateDto.Round(aggregate!.Average).Should().Be(expectedAverage);
        aggregate.Count.Should().Be(expectedCount);
        (await TripReviewAggregateReader.QueryConflictingLegacyPoiTargetIds(context)
                .AnyAsync(targetId => targetId == 1))
            .Should().BeFalse("equal numeric commerce/service IDs are distinct namespaces");
    }

    [SqlServerFact]
    public async Task Poi_CanonicalEditReplacesOneContributionWithoutChangingCount()
    {
        await using var database = await SeedAsync();
        await AddCanonicalPoiAsync(database, 1, 2);
        await using (var edit = database.CreateDbContext())
        {
            var review = await edit.TripReviews.SingleAsync();
            review.TryEditPublished(5, "Edited POI", "Edited service review", false,
                "Aggregate Owner", ReviewContentPolicy.ActiveVersion, Now.AddHours(1)).Should().BeTrue();
            await edit.SaveChangesAsync();
        }
        await using var context = database.CreateDbContext();

        var ratings = TripReviewAggregateReader.QueryPoiReviews(context)
            .Where(item => item.TargetId == 1);

        (await ratings.CountAsync()).Should().Be(1);
        TripReviewAggregateDto.Round(await ratings.AverageAsync(item => (decimal?)item.Rating))
            .Should().Be(5m);
    }

    [SqlServerFact]
    public async Task Poi_RolledBackCanonicalEditLeavesAggregateUnchanged()
    {
        await using var database = await SeedAsync();
        await AddCanonicalPoiAsync(database, 1, 2);
        await using (var edit = database.CreateDbContext())
        await using (var transaction = await edit.Database.BeginTransactionAsync())
        {
            var review = await edit.TripReviews.SingleAsync();
            review.TryEditPublished(5, "Edited POI", "Edited service review", false,
                "Aggregate Owner", ReviewContentPolicy.ActiveVersion, Now.AddHours(1)).Should().BeTrue();
            await edit.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using var context = database.CreateDbContext();

        var ratings = TripReviewAggregateReader.QueryPoiReviews(context)
            .Where(item => item.TargetId == 1);

        (await ratings.CountAsync()).Should().Be(1);
        TripReviewAggregateDto.Round(await ratings.AverageAsync(item => (decimal?)item.Rating))
            .Should().Be(2m);
    }

    [SqlServerFact]
    public async Task Poi_DetailAndExploreExposeTheSameCanonicalPlusLegacyAggregate()
    {
        await using var database = await SeedAsync();
        await AddCanonicalPoiAsync(database, 1, 5);
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,rating)
            VALUES(1,'POI',1,3);
            """);
        await using var detailContext = database.CreateDbContext();

        var detail = await new GetPoiDetailQueryHandler(
            detailContext,
            new Clock(Now),
            NullLogger<GetPoiDetailQueryHandler>.Instance)
            .Handle(new GetPoiDetailQuery(1), default);

        detail.IsSuccess.Should().BeTrue();
        detail.Value.AverageRating.Should().Be(4m);
        detail.Value.ReviewCount.Should().Be(2);
        await using var exploreContext = database.CreateDbContext();
        var explore = await new ExplorePoisQueryHandler(
            exploreContext,
            new Clock(Now),
            NullLogger<ExplorePoisQueryHandler>.Instance)
            .Handle(new ExplorePoisQuery(), default);
        var item = explore.Value.Items.Should().ContainSingle().Subject;
        item.AverageRating.Should().Be(4m);
        item.ReviewCount.Should().Be(2);
    }

    private static async Task<SqlServerTestDatabase> SeedAsync()
    {
        var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role,status,email,full_name)
            VALUES('Traveler','Active',N'aggregate@test.invalid',N'Aggregate Owner'),
                  ('TourOperator','Active',N'aggregate-operator@test.invalid',N'Operator');
            INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,approval_status)
            VALUES(2,N'Aggregate Operator',N'TM79-AGG-TAX',N'TM79-AGG-LICENSE','Approved');
            INSERT catalog.POICategories(name) VALUES(N'Aggregate Category');
            INSERT catalog.POIs(category_id,name,latitude,longitude,status)
            VALUES(1,N'Aggregate POI',10.000000,106.000000,'Active');
            INSERT commercial.ServiceProviders(name,service_category)
            VALUES(N'Aggregate Provider','Hotel');
            INSERT commercial.Services(provider_id,service_category,name,poi_id,price_amount,price_unit)
            VALUES(1,'Hotel',N'Aggregate Service',1,0,'PerNight');
            INSERT commercial.ServiceBookings(traveler_user_id,service_id,start_datetime,total_price,status,provider_reference)
            VALUES(1,1,'2026-09-29T00:00:00',0,'Completed',N'AGG-SERVICE-1');
            INSERT commerce.Tours(operator_user_id,title,base_price,duration_days,status)
            VALUES(2,N'Aggregate Tour',0,1,'Approved');
            INSERT planning.Itineraries(traveler_user_id,source_type,title)
            VALUES(1,'Manual',N'Aggregate First'),(1,'Manual',N'Aggregate Second');
            INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
            VALUES('TM79-AGG-1',1,1,0,0,'Completed'),('TM79-AGG-2',1,2,0,0,'Completed');
            """);
        return database;
    }

    private static async Task AddCanonicalAsync(SqlServerTestDatabase database, long bookingId, byte rating)
    {
        await using var context = database.CreateDbContext();
        context.TripReviews.Add(TripReview.CreatePublished(bookingId, 1, 1, null, rating,
            "Aggregate title", "Aggregate content", null, null, false,
            "Aggregate Owner", null, Now));
        await context.SaveChangesAsync();
    }

    private static async Task AddCanonicalPoiAsync(
        SqlServerTestDatabase database,
        long serviceBookingId,
        byte rating)
    {
        await using var context = database.CreateDbContext();
        context.TripReviews.Add(TripReview.CreatePublishedForService(
            serviceBookingId, 1, 1, rating, "Aggregate POI title", "Aggregate POI content",
            null, null, false, "Aggregate Owner", ReviewContentPolicy.ActiveVersion, Now));
        await context.SaveChangesAsync();
    }

    private sealed class Clock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}