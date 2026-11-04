using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Emergency;

/// <summary>
/// Exercises the production classifier ApplicationDbContext.IsSourceBookingUniqueConflict
/// against real SQL Server unique-index violations (SqlException cannot be constructed in
/// unit tests): a duplicate Itineraries.source_booking_id must classify as the BookedTour
/// replay conflict, while any other unique violation must not.
/// </summary>
public sealed class SourceBookingConflictClassifierSqlServerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DuplicateSourceBookingId_ClassifiesAsSourceBookingUniqueConflict()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using (var db = database.CreateDbContext())
        {
            var (traveler, tour, _, booking) = await SeedBookingAsync(db, "BK-UC70-CLS-0001");
            db.Itineraries.Add(Itinerary.CreateBookedTour(
                traveler.Id, booking.BookingCode, booking.Id, tour.Id, Now));
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            var (traveler, tour, _, booking) = await SeedBookingAsync(db, "BK-UC70-CLS-0001", reuseExisting: true);
            db.Itineraries.Add(Itinerary.CreateBookedTour(
                traveler.Id, booking.BookingCode, booking.Id, tour.Id, Now));
            var conflict = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.IsSourceBookingUniqueConflict(conflict).Should().BeTrue(
                "a duplicate Itineraries.source_booking_id is exactly the replay conflict");
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DuplicateBookingCode_DoesNotClassifyAsSourceBookingUniqueConflict()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var db = database.CreateDbContext();
        var (traveler, _, _, firstBooking) = await SeedBookingAsync(db, "BK-UC70-CLS-0002");

        var duplicate = Booking.Create(firstBooking.BookingCode, traveler.Id, firstBooking.TourScheduleId,
            null, 1_000_000m, 0m, 1_000_000m,
            [BookingParticipant.Create("Dupe", ParticipantType.Adult, null, null, null, null, null)],
            Now.AddMinutes(-5), Now.AddMinutes(15));
        db.Bookings.Add(duplicate);

        var conflict = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.IsSourceBookingUniqueConflict(conflict).Should().BeFalse(
            "a violation on another unique index must not be treated as the replay conflict");
    }

    private static async Task<(User Traveler, Tour Tour, TourSchedule Schedule, Booking Booking)> SeedBookingAsync(
        TripMate.Infrastructure.Persistence.ApplicationDbContext db, string bookingCode, bool reuseExisting = false)
    {
        if (reuseExisting)
        {
            var existingBooking = await db.Bookings.AsNoTracking()
                .SingleAsync(booking => booking.BookingCode == bookingCode);
            var existingSchedule = await db.TourSchedules.AsNoTracking()
                .SingleAsync(schedule => schedule.Id == existingBooking.TourScheduleId);
            var existingTour = await db.Tours.AsNoTracking()
                .SingleAsync(tour => tour.Id == existingSchedule.TourId);
            var existingTraveler = await db.Users.AsNoTracking()
                .SingleAsync(user => user.Id == existingBooking.TravelerUserId);
            return (existingTraveler, existingTour, existingSchedule, existingBooking);
        }

        var traveler = new User
        {
            Email = $"classifier-{Guid.NewGuid():N}@example.test",
            FullName = "UC70 Classifier Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var operatorUser = new User
        {
            Email = $"classifier-op-{Guid.NewGuid():N}@example.test",
            FullName = "UC70 Classifier Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Users.AddRange(traveler, operatorUser);
        await db.SaveChangesAsync();
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            User = operatorUser,
            CompanyName = "UC70 Classifier Tours",
            TaxCode = "UC70-CLS-TAX",
            BusinessLicenseNo = "UC70-CLS-LICENSE",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, operatorUser.Id);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        typeof(Tour).GetProperty(nameof(Tour.TimeZoneId))!.SetValue(tour, "Asia/Ho_Chi_Minh");
        typeof(Tour).GetProperty(nameof(Tour.DurationDays))!.SetValue(tour, 1);
        typeof(Tour).GetProperty(nameof(Tour.Title))!.SetValue(tour, "UC70 Classifier Tour");
        typeof(Tour).GetProperty(nameof(Tour.BasePrice))!.SetValue(tour, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();

        var schedule = TourSchedule.Create(Now.AddDays(4), Now.AddDays(4).AddHours(8), "Da Nang", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var booking = Booking.Create(bookingCode, traveler.Id, schedule.Id, null,
            1_000_000m, 0m, 1_000_000m,
            [BookingParticipant.Create("UC70 Traveler", ParticipantType.Adult, null, null, null, null, null)],
            Now.AddMinutes(-5), Now.AddMinutes(15));
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return (traveler, tour, schedule, booking);
    }
}
