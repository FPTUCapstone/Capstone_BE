using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.GetContext;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class TripReviewContextSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task OwnedMissingForeignAndInvalidBookingIds_AreIsolatedAndReadOnly()
    {
        await using var db = await SeedAsync();
        await using var context = db.CreateDbContext();
        var reader = new SqlServerTripReviewContextReader(context);
        (await reader.ReadOwnedAsync(1, 1, default)).Should().NotBeNull();
        (await reader.ReadOwnedAsync(1, 2, default)).Should().BeNull();
        (await reader.ReadOwnedAsync(999999, 1, default)).Should().BeNull();
        (await reader.ReadOwnedAsync(-1, 1, default)).Should().BeNull();
        context.ChangeTracker.Entries().Should().BeEmpty();
        (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviews")).Should().Be(0);
        (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.Reviews")).Should().Be(0);
    }

    [SqlServerTheory]
    [InlineData("Completed", true)]
    [InlineData("Confirmed", false)]
    [InlineData("PendingPayment", false)]
    [InlineData("Cancelled", false)]
    [InlineData("Expired", false)]
    public async Task BookingCompletionIsAuthority_NotPaymentOrItineraryStatus(string status, bool eligible)
    {
        await using var db = await SeedAsync();
        // The values come only from this fixed test theory, not an external request.
        await db.ExecuteNonQueryAsync($"UPDATE commerce.Bookings SET status='{status}';");
        await using var context = db.CreateDbContext();
        var value = (await Handler(context).Handle(new(1), default)).Value;
        value.CanSubmit.Should().Be(eligible);
        value.Subject!.Kind.Should().Be(TripReviewContextValues.ItinerarySubject);
        value.PoiRatings.Available.Should().BeFalse();
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task ScheduleOnlyAndBothLinks_ResolveTourWithoutInventingRouteHistory()
    {
        await using var db = await SeedAsync();
        await db.ExecuteNonQueryAsync("""
            INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no) VALUES(3,N'Test',N'Tax',N'License');
            INSERT commerce.Tours(operator_user_id,title,base_price) VALUES(3,N'Tour',0);
            INSERT commerce.TourSchedules(tour_id,start_datetime,end_datetime,total_capacity) VALUES(1,'2026-09-01','2026-09-02',10);
            UPDATE commerce.Bookings SET tour_schedule_id=1;
            """);
        await using var context = db.CreateDbContext();
        var both = (await Handler(context).Handle(new(1), default)).Value;
        both.Subject.Should().Be(new TripReviewSubjectDto(TripReviewContextValues.TourSubject, 1, null));
        both.CanSubmit.Should().BeTrue();
        both.RoutePacing.Reason.Should().Be(TripReviewContextValues.RouteContextUnavailable);
        await db.ExecuteNonQueryAsync("UPDATE commerce.Bookings SET itinerary_id=NULL;");
        var schedule = (await Handler(context).Handle(new(1), default)).Value;
        schedule.Subject.Should().Be(both.Subject);
        schedule.CanSubmit.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task ForeignItineraryAndMissingSubject_NeverExposeOtherOwnerContext()
    {
        await using var db = await SeedAsync();
        await db.ExecuteNonQueryAsync("UPDATE planning.Itineraries SET traveler_user_id=2;");
        await using var context = db.CreateDbContext();
        var foreign = (await Handler(context).Handle(new(1), default)).Value;
        foreign.Subject.Should().BeNull();
        foreign.SubmitUnavailableReason.Should().Be(TripReviewContextValues.InconsistentContext);
        await db.ExecuteNonQueryAsync("UPDATE commerce.Bookings SET itinerary_id=NULL;");
        var missing = (await Handler(context).Handle(new(1), default)).Value;
        missing.Subject.Should().BeNull();
        missing.SubmitUnavailableReason.Should().Be(TripReviewContextValues.InconsistentContext);
    }

    [SqlServerFact]
    public async Task OwnedCspProvenance_IsIndependentOfUnverifiedVisitAndRouteEvidence()
    {
        await using var db = await SeedAsync();
        await db.ExecuteNonQueryAsync("""
            INSERT planning.SchedulingRequests(traveler_user_id,idempotency_key,request_hash,start_at,start_latitude,start_longitude,
                destination_latitude,destination_longitude,available_minutes,search_radius_km)
            VALUES(1,NEWID(),REPLICATE('a',64),'2026-09-01',16,108,16,108,60,2);
            UPDATE planning.Itineraries SET source_type='CSPGenerated',scheduling_request_id=1;
            """);
        await using var context = db.CreateDbContext();
        var owned = (await Handler(context).Handle(new(1), default)).Value;
        owned.CspRating.Available.Should().BeTrue();
        owned.PoiRatings.Available.Should().BeFalse();
        owned.RoutePacing.Available.Should().BeFalse();
        await db.ExecuteNonQueryAsync("UPDATE planning.SchedulingRequests SET traveler_user_id=2;");
        var foreign = (await Handler(context).Handle(new(1), default)).Value;
        foreign.CspRating.Reason.Should().Be(TripReviewContextValues.CspProvenanceUnavailable);
        foreign.CanSubmit.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task NewReview_IsRecoveredAfterDeadlineWithRealBase64VersionAndNoTracking()
    {
        await using var db = await SeedAsync();
        await using (var writer = db.CreateDbContext())
        {
            writer.TripReviews.Add(TripReview.CreatePublished(1, 1, null, 1, 4, "Title", "Content", null, null,
                false, "Original Name", "test-policy", Now.AddDays(-8)));
            await writer.SaveChangesAsync();
        }
        await using var context = db.CreateDbContext();
        var value = (await Handler(context).Handle(new(1), default)).Value;
        value.CanSubmit.Should().BeFalse();
        value.ExistingReviewKind.Should().Be(TripReviewContextValues.New);
        value.EditableFields.Should().BeEmpty();
        var saved = value.Review.Should().BeOfType<NewTripReviewDto>().Subject;
        Convert.FromBase64String(saved.Version).Should().HaveCount(8);
        saved.PublicDisplayName.Should().Be("O. N.");
        saved.Content.Should().Be("Content");
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task LegacyRowsRemainUnchanged_ForeignAuthorContentIsNotReturned()
    {
        await using var db = await SeedAsync();
        await db.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating,comment)
            VALUES(1,'POI',10,1,4,N'Own legacy'),(2,'POI',11,1,2,N'Foreign private text');
            """);
        await using var context = db.CreateDbContext();
        var value = (await Handler(context).Handle(new(1), default)).Value;
        value.SubmitUnavailableReason.Should().Be(TripReviewContextValues.LegacyConflict);
        var legacy = value.Review.Should().BeOfType<LegacyTripReviewDto>().Subject;
        legacy.Entries.Should().ContainSingle().Which.Comment.Should().Be("Own legacy");
        (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.Reviews")).Should().Be(2);
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task ReaderAndHandlerRejectInactiveTravelerBeforeReturningContext()
    {
        await using var db = await SeedAsync();
        await db.ExecuteNonQueryAsync("UPDATE dbo.Users SET status='Locked' WHERE user_id=1;");
        await using var context = db.CreateDbContext();
        (await new SqlServerTripReviewContextReader(context).ReadOwnedAsync(1, 1, default)).Should().BeNull();
        (await Handler(context).Handle(new(1), default)).ErrorCode.Should().Be(TripReviewErrorCodes.Forbidden);
    }

    [SqlServerFact]
    public async Task CommerceAndServiceWithSameNumericId_ReturnTypedSubjectsAndServerSummary()
    {
        await using var db = await SeedAsync();
        await db.ExecuteNonQueryAsync("""
            INSERT commercial.ServiceProviders(name,service_category)
            VALUES(N'Provider','Hotel');
            INSERT catalog.POICategories(name) VALUES(N'Test category');
            INSERT catalog.POIs(category_id,name,latitude,longitude)
            VALUES(1,N'Authoritative POI',16,108);
            INSERT commercial.Services(provider_id,service_category,name,poi_id,price_amount,price_unit)
            VALUES(1,'Hotel',N'Authoritative Service',1,0,'PerNight');
            INSERT commercial.ServiceBookings(traveler_user_id,service_id,start_datetime,total_price,status,provider_reference)
            VALUES(1,1,'2026-10-01T02:00:00',0,'Completed',N'SB-123');
            """);
        await using var context = db.CreateDbContext();
        var handler = Handler(context);

        var commerce = (await handler.Handle(
            new(ReviewableRecordRef.CommerceBooking(1)), default)).Value;
        var service = (await handler.Handle(
            new(ReviewableRecordRef.ServiceBooking(1)), default)).Value;

        commerce.ReviewableRecord.Should().Be(ReviewableRecordRef.CommerceBooking(1));
        service.ReviewableRecord.Should().Be(ReviewableRecordRef.ServiceBooking(1));
        service.Subject.Should().Be(new TripReviewSubjectDto(TripReviewContextValues.PoiSubject, 1));
        service.Summary.Should().Be(new TripReviewSummaryDto(
            "Authoritative Service", new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero), "SB-123"));
    }

    [SqlServerFact]
    public async Task ServiceNullPoi_IsUnsupported_AndForeignOrIncompleteRemainNondisclosing()
    {
        await using var db = await SeedAsync();
        await db.ExecuteNonQueryAsync("""
            INSERT commercial.ServiceProviders(name,service_category)
            VALUES(N'Provider','Vehicle');
            INSERT commercial.Services(provider_id,service_category,name,price_amount,price_unit)
            VALUES(1,'Vehicle',N'No POI Service',0,'PerDay');
            INSERT commercial.ServiceBookings(traveler_user_id,service_id,start_datetime,total_price,status)
            VALUES(1,1,'2026-10-01T02:00:00',0,'Completed');
            """);
        await using var context = db.CreateDbContext();
        var handler = Handler(context);

        var unsupported = await handler.Handle(
            new(ReviewableRecordRef.ServiceBooking(1)), default);
        unsupported.ErrorCode.Should().Be(TripReviewErrorCodes.UnsupportedSubject);

        await db.ExecuteNonQueryAsync("UPDATE commercial.ServiceBookings SET status='Confirmed';");
        var incomplete = await handler.Handle(
            new(ReviewableRecordRef.ServiceBooking(1)), default);
        incomplete.ErrorCode.Should().Be(TripReviewErrorCodes.UnsupportedSubject);

        var foreign = await new SqlServerTripReviewContextReader(context).ReadOwnedAsync(
            ReviewableRecordRef.ServiceBooking(1), 2, default);
        foreign.Should().BeNull();
    }

    private static GetTripReviewContextQueryHandler Handler(ApplicationDbContext context) =>
        new(context, new CurrentUser(), new Clock(), new SqlServerTripReviewContextReader(context));

    private sealed class CurrentUser : ICurrentUserService
    {
        public long? UserId => 1;
        public string? Role => nameof(UserRole.Traveler);
    }

    private sealed class Clock : IDateTimeProvider { public DateTimeOffset UtcNow => Now; }

    private static async Task<SqlServerTestDatabase> SeedAsync()
    {
        var db = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await db.ExecuteNonQueryAsync("""
                INSERT dbo.Users(role,status,email,full_name)
                VALUES('Traveler','Active',N'owner@test.invalid',N'Nguyễn Tiến Đạt'),
                      ('Traveler','Active',N'foreign@test.invalid',N'Foreign Profile'),
                      ('TourOperator','Active',N'operator@test.invalid',N'Operator');
                INSERT planning.Itineraries(traveler_user_id,source_type,title) VALUES(1,'Manual',N'Owned');
                INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
                VALUES('TM79-CONTEXT',1,1,0,0,'Completed');
                """);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
}