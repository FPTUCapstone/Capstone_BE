using System.Reflection;

using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Bookings;

/// <summary>Shared seed helpers for UC-27 quote/create handler tests.</summary>
internal static class TourBookingTestData
{
    public static async Task<User> SeedTravelerAsync(TestDbContext db, long id)
    {
        var user = new User
        {
            Id = id, Email = $"traveler{id}@test.local", FullName = "Traveler",
            Role = UserRole.Traveler, Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static Tour SeedTour(long operatorId, decimal basePrice, TourStatus status = TourStatus.Published)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorId);
        Set(tour, nameof(Tour.Title), "Test Tour");
        Set(tour, nameof(Tour.BasePrice), basePrice);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), status);
        return tour;
    }

    public static TourSchedule SeedSchedule(Tour tour, DateTimeOffset startAtUtc, int totalCapacity,
        int reservedCapacity, decimal? adultPrice = null, decimal? childPrice = null, decimal? infantPrice = null,
        DateTimeOffset? registrationDeadlineAtUtc = null, TourScheduleStatus status = TourScheduleStatus.Scheduled)
    {
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), startAtUtc);
        Set(schedule, nameof(TourSchedule.EndAtUtc), startAtUtc.AddDays(1));
        Set(schedule, nameof(TourSchedule.TotalCapacity), totalCapacity);
        Set(schedule, nameof(TourSchedule.MinimumCapacity), 1);
        Set(schedule, nameof(TourSchedule.ReservedCapacity), reservedCapacity);
        Set(schedule, nameof(TourSchedule.AdultPrice), adultPrice);
        Set(schedule, nameof(TourSchedule.ChildPrice), childPrice);
        Set(schedule, nameof(TourSchedule.InfantPrice), infantPrice);
        Set(schedule, nameof(TourSchedule.RegistrationDeadlineAtUtc), registrationDeadlineAtUtc);
        Set(schedule, nameof(TourSchedule.Status), status);
        Set(schedule, nameof(TourSchedule.CreatedAtUtc), startAtUtc.AddDays(-30));
        return schedule;
    }

    public static Voucher SeedVoucher(string code, VoucherDiscountType discountType, decimal discountValue,
        decimal? maxDiscountAmount, decimal minOrderAmount, int? usageLimit, int? usageLimitPerUser,
        DateTimeOffset validFromUtc, DateTimeOffset validToUtc, int usedCount, IReadOnlyCollection<Tour> applicableTours)
    {
        var voucher = Voucher.Create(1, code, discountType, discountValue, maxDiscountAmount, minOrderAmount,
            usageLimit, usageLimitPerUser, validFromUtc, validToUtc, applicableTours, validFromUtc.AddDays(-1));
        Set(voucher, nameof(Voucher.UsedCount), usedCount);
        return voucher;
    }

    public static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}
