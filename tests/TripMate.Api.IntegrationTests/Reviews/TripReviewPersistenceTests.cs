using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class TripReviewPersistenceTests
{
    private static readonly DateTimeOffset Created = new DateTimeOffset(2026, 9, 28, 15, 0, 0, TimeSpan.FromHours(7)).AddTicks(1234567);

    private static TripReview Create(long booking = 1, long? tour = null, long? itinerary = 1,
        RoutePacingFeedback? pacing = RoutePacingFeedback.TooLoose, byte? csp = 2, string? policy = null) =>
        TripReview.CreatePublished(booking, 1, tour, itinerary, 5, "Chuyến đi", "Nội dung chuyến đi",
            pacing, csp, false, "Nguyễn Tiến Đạt", policy, Created);

    private static TripReview CreateService() =>
        TripReview.CreatePublishedForService(1, 1, 1, 5, "Dịch vụ", "Nội dung dịch vụ",
            null, null, false, "Nguyễn Tiến Đạt", null, Created);

    [SqlServerTheory]
    [InlineData(null)]
    [InlineData("tm79-review-text-v1")]
    public async Task NewContext_RoundTripsAllParentFieldsAndUtcPrecision(string? policy)
    {
        await using var database = await SeedAsync();
        var entity = Create(policy: policy);
        await using (var writer = database.CreateDbContext())
        {
            IApplicationDbContext port = writer;
            port.TripReviews.Add(entity);
            await port.SaveChangesAsync(default);
        }
        entity.Id.Should().BeGreaterThan(0);
        entity.Version.Should().HaveCount(8);
        await using var reader = database.CreateDbContext();
        var saved = await reader.TripReviews.AsNoTracking().SingleAsync();
        saved.Should().BeEquivalentTo(entity);
        saved.PolicyVersion.Should().Be(policy);
        reader.Model.FindEntityType(typeof(TripReview))!.FindProperty(nameof(TripReview.PolicyVersion))!
            .IsNullable.Should().BeTrue();
        saved.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        saved.CreatedAtUtc.Ticks.Should().Be(Created.UtcTicks);
        saved.EditDeadlineUtc.Ticks.Should().Be(Created.AddDays(7).UtcTicks);
        saved.UpdatedAtUtc.Ticks.Should().Be(Created.UtcTicks);
        (await reader.Reviews.CountAsync()).Should().Be(0, "the parent is not mirrored into legacy Reviews");
        (await database.ExecuteScalarAsync<string>("SELECT route_pacing FROM social.TripReviews")).Should().Be("tooLoose");
    }

    [SqlServerFact]
    public async Task TourSubjectAndNullableC4_RoundTripWithoutCreatingItineraryTarget()
    {
        await using var database = await SeedAsync();
        await using (var writer = database.CreateDbContext())
        {
            writer.TripReviews.Add(Create(tour: 1, itinerary: null, pacing: null, csp: null));
            await writer.SaveChangesAsync();
        }
        await using var reader = database.CreateDbContext();
        var saved = await reader.TripReviews.SingleAsync();
        saved.TourId.Should().Be(1);
        saved.ItineraryId.Should().BeNull();
        saved.RoutePacing.Should().BeNull();
        saved.CspRating.Should().BeNull();
    }

    [SqlServerFact]
    public async Task SeparateContexts_RejectStaleRowversionAndPreserveWinningEdit()
    {
        await using var database = await SeedAsync();
        await using (var seed = database.CreateDbContext())
        {
            seed.TripReviews.Add(Create());
            await seed.SaveChangesAsync();
        }
        await using var first = database.CreateDbContext();
        await using var second = database.CreateDbContext();
        var winner = await first.TripReviews.SingleAsync();
        var stale = await second.TripReviews.SingleAsync();
        var oldVersion = winner.Version.ToArray();
        winner.TryEditPublished(4, "Winner", "Accepted winner", true, "Current Name", "p2", Created.AddDays(1));
        await first.SaveChangesAsync();
        winner.Version.Should().NotEqual(oldVersion);
        stale.TryEditPublished(1, "Loser", "Accepted loser", true, "Stale Name", "p3", Created.AddDays(2));
        Func<Task> save = () => second.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using var verify = database.CreateDbContext();
        var saved = await verify.TripReviews.AsNoTracking().SingleAsync();
        saved.Title.Should().Be("Winner");
        saved.PolicyVersion.Should().Be("p2");
        saved.PublicDisplayName.Should().Be("Current Name");
        saved.Version.Should().Equal(winner.Version);
        saved.CreatedAtUtc.Should().Be(Created);
        saved.EditDeadlineUtc.Should().Be(Created.AddDays(7));
        saved.RoutePacing.Should().Be(RoutePacingFeedback.TooLoose);
        saved.CspRating.Should().Be(2);
    }

    [SqlServerFact]
    public async Task TransactionRollback_LeavesParentAndLegacyUnchanged()
    {
        await using var database = await SeedAsync();
        await using (var writer = database.CreateDbContext())
        {
            await using var transaction = await writer.Database.BeginTransactionAsync();
            writer.TripReviews.Add(Create());
            await writer.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using var reader = database.CreateDbContext();
        (await reader.TripReviews.CountAsync()).Should().Be(0);
        (await reader.Reviews.CountAsync()).Should().Be(0);
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedParentUniqueViolation_IsClassifiedAsDuplicate(bool service)
    {
        await using var database = await SeedAsync();
        await using (var first = database.CreateDbContext())
        {
            first.TripReviews.Add(service ? CreateService() : Create());
            await first.SaveChangesAsync();
        }

        await using var duplicate = database.CreateDbContext();
        duplicate.TripReviews.Add(service ? CreateService() : Create());
        Func<Task> save = () => duplicate.SaveChangesAsync();
        var failure = await save.Should().ThrowAsync<DbUpdateException>();

        new SqlServerTripReviewPersistenceErrorClassifier()
            .IsBookingDuplicate(failure.Which).Should().BeTrue();
    }

    [SqlServerFact]
    public async Task UnscreenedEdit_RoundTripsNullWithoutRetainingPreviousPolicyApproval()
    {
        await using var database = await SeedAsync();
        await using (var writer = database.CreateDbContext())
        {
            writer.TripReviews.Add(Create(policy: "tm79-review-text-v1"));
            await writer.SaveChangesAsync();
        }
        await using (var editor = database.CreateDbContext())
        {
            var review = await editor.TripReviews.SingleAsync();
            review.TryEditPublished(4, "Edited", "Unscreened edited content", false,
                "Changed Profile", null, Created.AddDays(1)).Should().BeTrue();
            await editor.SaveChangesAsync();
        }
        await using var reader = database.CreateDbContext();
        var saved = await reader.TripReviews.AsNoTracking().SingleAsync();
        saved.PolicyVersion.Should().BeNull();
        saved.Content.Should().Be("Unscreened edited content");
        saved.CreatedAtUtc.Should().Be(Created);
        saved.EditDeadlineUtc.Should().Be(Created.AddDays(7));
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviews WHERE policy_version IS NULL"))
            .Should().Be(1);
    }

    private static async Task<SqlServerTestDatabase> SeedAsync()
    {
        var database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.ExecuteNonQueryAsync("""
                INSERT dbo.Users(role,email,full_name) VALUES('Traveler',N'parent@test.invalid',N'Nguyễn Tiến Đạt');
                INSERT dbo.Users(role,email,full_name) VALUES('TourOperator',N'operator@test.invalid',N'Operator');
                INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no) VALUES(2,N'Test',N'Tax',N'License');
                INSERT commerce.Tours(operator_user_id,title,base_price) VALUES(2,N'Tour',0);
                INSERT planning.Itineraries(traveler_user_id,source_type,title) VALUES(1,'Manual',N'Test');
                INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
                VALUES('TM79-PERSISTENCE',1,1,0,0,'Completed');
                INSERT catalog.POICategories(name) VALUES(N'Test category');
                INSERT catalog.POIs(category_id,name,latitude,longitude)
                VALUES(1,N'Canonical POI',16,108);
                INSERT commercial.ServiceProviders(name,service_category)
                VALUES(N'Provider','Hotel');
                INSERT commercial.Services(provider_id,service_category,name,poi_id,price_amount,price_unit)
                VALUES(1,'Hotel',N'Service One',1,0,'PerNight');
                INSERT commercial.ServiceBookings(traveler_user_id,service_id,start_datetime,total_price,status,provider_reference)
                VALUES(1,1,'2026-10-01T02:00:00',0,'Completed',N'SB-PERSISTENCE');
                """);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }
}