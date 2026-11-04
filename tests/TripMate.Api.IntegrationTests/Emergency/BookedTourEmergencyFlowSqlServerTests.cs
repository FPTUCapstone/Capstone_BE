using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Navigation.Start;
using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Emergency;

/// <summary>
/// UC-70 end-to-end on real SQL Server: payment confirmation materializes the navigable
/// BookedTour itinerary, the traveler starts navigation from exactly that itinerary, the
/// operator impact preview sees the session, and the emergency stop interrupts it.
/// No TripSession is ever seeded by hand.
/// </summary>
public sealed class BookedTourEmergencyFlowSqlServerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    // 2026-11-01T03:00Z = 10:00 Asia/Ho_Chi_Minh; the departure is already running.
    private static readonly DateTimeOffset DepartureStartUtc =
        new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);
    private const string OperatorPassword = "OperatorPass123!";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Payment_MaterializesNavigableItineraryAndEmergencyStopInterruptsItsSession()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (operatorId, travelerId, booking, transactionId, poiOneId, poiTwoId, tourId) =
            await SeedPaidBookingAsync(database);
        await using var db = database.CreateDbContext();

        var result = await new PaymentCompletionProcessor(db, new FixedClock(Now))
            .CompleteAsync(transactionId, null, PaymentTransactionStatus.Success,
                "gateway-ref-1", "{}", null, null, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();

        var itinerary = await db.Itineraries
            .Include(entity => entity.Items)
            .SingleAsync(entity => entity.SourceBookingId == booking.Id);
        itinerary.SourceType.Should().Be(Itinerary.BookedTourSourceType);
        itinerary.SourceTourId.Should().Be(tourId);
        itinerary.Status.Should().Be(Itinerary.ActiveStatus);
        itinerary.ValidFromUtc.Should().Be(DepartureStartUtc);
        itinerary.ValidToUtc.Should().Be(DepartureStartUtc.AddDays(2));
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToList();
        items.Should().HaveCount(2);
        // Day 1: 2026-11-01 10:30 +07 -> 03:30Z; Day 2: 2026-11-02 09:00 +07 -> 02:00Z.
        items[0].PointOfInterestId.Should().Be(poiOneId);
        items[0].PlannedArrivalUtc.Should().Be(new DateTimeOffset(2026, 11, 1, 3, 30, 0, TimeSpan.Zero));
        items[0].PlannedDepartureUtc.Should().Be(new DateTimeOffset(2026, 11, 1, 4, 30, 0, TimeSpan.Zero));
        items[0].IsMandatory.Should().BeTrue();
        items[1].PointOfInterestId.Should().Be(poiTwoId);
        items[1].PlannedArrivalUtc.Should().Be(new DateTimeOffset(2026, 11, 2, 2, 0, 0, TimeSpan.Zero));

        // The traveler starts navigation from exactly the payment-produced itinerary.
        var navigation = await new StartNavigationSessionCommandHandler(
                db, new ItineraryAccessService(db), new FixedClock(Now))
            .Handle(new StartNavigationSessionCommand(itinerary.Id, travelerId, Guid.NewGuid()),
                CancellationToken.None);
        navigation.IsSuccess.Should().BeTrue("payment output must be navigable: {0}", navigation.ErrorMessage);
        navigation.Value.Items.Should().HaveCount(2);

        var impact = await new GetEmergencyTourImpactQueryHandler(db, OperatorUser(operatorId), new FixedClock(Now))
            .Handle(new GetEmergencyTourImpactQuery(tourId, booking.TourScheduleId), CancellationToken.None);
        impact.IsSuccess.Should().BeTrue();
        impact.Value.ActiveSessionCount.Should().Be(1);
        impact.Value.HasUnverifiedSessions.Should().BeFalse();

        var stop = await new EmergencyTourCommandHandler(db, OperatorUser(operatorId), new FixedClock(Now),
                new PasswordHasherService())
            .Handle(new EmergencyStopActiveCommand(tourId, booking.TourScheduleId, Guid.NewGuid(),
                impact.Value.ImpactVersion, OperatorPassword, EmergencyTourReason.SafetyIncident,
                "Rockslide blocks the mountain pass.",
                "Stay with the guide at the meeting point.", "0900000000"), CancellationToken.None);
        stop.IsSuccess.Should().BeTrue("{0}", stop.ErrorMessage);

        var session = await db.TripSessions.AsNoTracking().SingleAsync();
        session.FsmState.Should().Be(TripSession.InterruptedState);
        var reloadedBooking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id);
        reloadedBooking.Status.Should().Be(BookingStatus.Interrupted);
        (await db.TourSchedules.AsNoTracking().SingleAsync(s => s.Id == booking.TourScheduleId))
            .Status.Should().Be(TourScheduleStatus.EmergencyStopped);

        // The itinerary stays Active, so the stop must also keep a new session from starting.
        db.ChangeTracker.Clear();
        var restart = await new StartNavigationSessionCommandHandler(
                db, new ItineraryAccessService(db), new FixedClock(Now))
            .Handle(new StartNavigationSessionCommand(itinerary.Id, travelerId, Guid.NewGuid()),
                CancellationToken.None);
        restart.IsFailure.Should().BeTrue();
        restart.ErrorCode.Should().Be(NavigationErrorCodes.SessionInterrupted);
        (await db.TripSessions.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ReplayPayment_DoesNotDuplicateItineraryOrItems()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (_, _, booking, transactionId, _, _, _) = await SeedPaidBookingAsync(database);

        await using (var db = database.CreateDbContext())
        {
            (await new PaymentCompletionProcessor(db, new FixedClock(Now))
                .CompleteAsync(transactionId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await using (var db = database.CreateDbContext())
        {
            (await new PaymentCompletionProcessor(db, new FixedClock(Now))
                .CompleteAsync(transactionId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await using var verify = database.CreateDbContext();
        var itinerary = await verify.Itineraries
            .Include(entity => entity.Items)
            .SingleAsync(entity => entity.SourceBookingId == booking.Id);
        itinerary.Items.Should().HaveCount(2);
        (await verify.Itineraries.CountAsync(i => i.SourceBookingId == booking.Id)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentCompletions_CreateExactlyOneItineraryAndBothCallsSucceed()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (_, _, booking, transactionId, _, _, _) = await SeedPaidBookingAsync(database);

        var firstTask = Task.Run(async () =>
        {
            await using var db = database.CreateDbContext();
            return await new PaymentCompletionProcessor(db, new FixedClock(Now))
                .CompleteAsync(transactionId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, CancellationToken.None);
        });
        var secondTask = Task.Run(async () =>
        {
            await using var db = database.CreateDbContext();
            return await new PaymentCompletionProcessor(db, new FixedClock(Now))
                .CompleteAsync(transactionId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, CancellationToken.None);
        });

        (await firstTask).IsSuccess.Should().BeTrue();
        (await secondTask).IsSuccess.Should().BeTrue();

        await using var verify = database.CreateDbContext();
        (await verify.Itineraries.CountAsync(i => i.SourceBookingId == booking.Id)).Should().Be(1);
        (await verify.ItineraryItems
            .CountAsync(item => item.Itinerary!.SourceBookingId == booking.Id)).Should().Be(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SnapshotSurvivesTemplateEditAfterPayment()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (_, _, booking, transactionId, _, _, _) = await SeedPaidBookingAsync(database);
        await using (var db = database.CreateDbContext())
        {
            (await new PaymentCompletionProcessor(db, new FixedClock(Now))
                .CompleteAsync(transactionId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        // The operator edits the tour template after the traveler paid.
        await database.ExecuteNonQueryAsync(
            "UPDATE commerce.TourItineraryItems SET planned_time = '15:00', stay_duration_minutes = 120;");

        await using var verify = database.CreateDbContext();
        var itinerary = await verify.Itineraries
            .Include(entity => entity.Items)
            .SingleAsync(entity => entity.SourceBookingId == booking.Id);
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToList();
        items[0].PlannedArrivalUtc.Should().Be(new DateTimeOffset(2026, 11, 1, 3, 30, 0, TimeSpan.Zero),
            "the paid snapshot is immutable, template edits never rewrite it");
        items[1].PlannedArrivalUtc.Should().Be(new DateTimeOffset(2026, 11, 2, 2, 0, 0, TimeSpan.Zero));
    }

    private static async Task<(long OperatorId, long TravelerId, Booking Booking, long TransactionId, long PoiOneId, long PoiTwoId, long TourId)>
        SeedPaidBookingAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var hasher = new PasswordHasherService();

        // dbo.Users etc. are IDENTITY tables, so generated ids are read back after each save.
        var operatorUser = new User
        {
            Email = "operator@example.test",
            FullName = "UC70 Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            PasswordHash = hasher.Hash(OperatorPassword),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Users.Add(operatorUser);
        var traveler = new User
        {
            Email = "traveler@example.test",
            FullName = "UC70 Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Users.Add(traveler);
        var category = PoiCategory.Create("Beach", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();

        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            User = operatorUser,
            CompanyName = "UC70 Tours",
            TaxCode = "UC70-TAX",
            BusinessLicenseNo = "UC70-LICENSE",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });

        var poiOne = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Name))!.SetValue(poiOne, "Golden Bridge");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Latitude))!.SetValue(poiOne, 16.0m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Longitude))!.SetValue(poiOne, 108.0m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.CategoryId))!.SetValue(poiOne, category.Id);
        var poiTwo = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Name))!.SetValue(poiTwo, "Marble Mountains");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Latitude))!.SetValue(poiTwo, 16.0m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Longitude))!.SetValue(poiTwo, 108.2m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.CategoryId))!.SetValue(poiTwo, category.Id);
        db.PointsOfInterest.AddRange(poiOne, poiTwo);
        await db.SaveChangesAsync();

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, operatorUser.Id);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        typeof(Tour).GetProperty(nameof(Tour.TimeZoneId))!.SetValue(tour, "Asia/Ho_Chi_Minh");
        typeof(Tour).GetProperty(nameof(Tour.DurationDays))!.SetValue(tour, 2);
        typeof(Tour).GetProperty(nameof(Tour.Title))!.SetValue(tour, "UC70 Two-Day Tour");
        typeof(Tour).GetProperty(nameof(Tour.BasePrice))!.SetValue(tour, 2_500_000m);
        var itemsField = typeof(Tour).GetField("_itineraryItems", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<TourItineraryItem>)itemsField.GetValue(tour)!).AddRange(
        [
            TourItineraryItem.Create(1, 1, poiOne.Id, new TimeOnly(10, 30), 60, "Golden Bridge walk"),
            TourItineraryItem.Create(2, 1, poiTwo.Id, new TimeOnly(9, 0), 45, null),
        ]);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();

        var schedule = TourSchedule.Create(DepartureStartUtc, DepartureStartUtc.AddDays(2), "Da Nang", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var participant = BookingParticipant.Create("UC70 Traveler", ParticipantType.Adult,
            null, null, null, null, null);
        var booking = Booking.Create("BK-UC70-E2E-0001", traveler.Id, schedule.Id, null,
            2_500_000m, 0m, 2_500_000m, [participant], Now.AddMinutes(-5), Now.AddMinutes(15));
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var transaction = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock,
            booking.TotalAmount, 9997, Now);
        db.PaymentTransactions.Add(transaction);
        await db.SaveChangesAsync();
        return (operatorUser.Id, traveler.Id, booking, transaction.Id, poiOne.Id, poiTwo.Id, tour.Id);
    }

    private static OperatorUserService OperatorUser(long userId) => new(userId);

    private sealed class OperatorUserService(long userId) : ICurrentUserService
    {
        public long? UserId => userId;
        public string? Role => nameof(UserRole.TourOperator);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}
