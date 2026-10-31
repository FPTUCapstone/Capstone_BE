using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Create;
using TripMate.Application.Features.Traveler.Bookings.Expiry;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Traveler;

/// <summary>Verifies that the expiry processor uses the real SQL Server transaction path.</summary>
public sealed class TravelerTourBookingExpirySqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExpireDueBookings_OnSqlServer_ExpiresOnceAndReturnsScheduleCapacity()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (bookingId, scheduleId) = await SeedDueBookingAsync(database);

        await using (var context = database.CreateDbContext())
        {
            var processor = new PendingBookingExpiryProcessor(context, new FixedClock(), new TourBookingOptions());
            (await processor.ExpireDueBookingsAsync(CancellationToken.None)).ExpiredCount.Should().Be(1);
            (await processor.ExpireDueBookingsAsync(CancellationToken.None)).ExpiredCount.Should().Be(0);
        }

        await using var verify = database.CreateDbContext();
        (await verify.Bookings.AsNoTracking().SingleAsync(booking => booking.Id == bookingId)).Status
            .Should().Be(BookingStatus.Expired);
        (await verify.TourSchedules.AsNoTracking().SingleAsync(schedule => schedule.Id == scheduleId)).ReservedCapacity
            .Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExpireDueBookingWithVoucher_OnSqlServer_ReleasesSlotOnceAndKeepsRedemption()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (bookingId, scheduleId) = await SeedDueBookingAsync(database);
        long voucherId;

        await using (var setup = database.CreateDbContext())
        {
            var booking = await setup.Bookings.SingleAsync(candidate => candidate.Id == bookingId);
            var tour = await setup.TourSchedules.Where(candidate => candidate.Id == scheduleId)
                .Select(candidate => candidate.Tour).SingleAsync();
            var voucher = Voucher.Create(tour.OperatorUserId, "SQLSAVE10", "SQL expiry coupon",
                VoucherDiscountType.Percentage, 10m, 500_000m, 0m, 1, 1,
                Now.AddDays(-1), Now.AddDays(1), [tour], Now.AddDays(-2));
            setup.Vouchers.Add(voucher);
            await setup.SaveChangesAsync();
            voucherId = voucher.Id;
            Set(booking, nameof(Booking.VoucherId), voucherId);
            Set(booking, nameof(Booking.DiscountAmount), 100_000m);
            Set(booking, nameof(Booking.TotalAmount), 900_000m);
            voucher.IncrementUsage();
            setup.VoucherRedemptions.Add(VoucherRedemption.Create(voucher, booking, 100_000m,
                Now.AddMinutes(-20)));
            await setup.SaveChangesAsync();
        }

        await using (var context = database.CreateDbContext())
        {
            var processor = new PendingBookingExpiryProcessor(context, new FixedClock(), new TourBookingOptions());
            (await processor.ExpireDueBookingsAsync(CancellationToken.None)).ExpiredCount.Should().Be(1);
            (await processor.ExpireDueBookingsAsync(CancellationToken.None)).ExpiredCount.Should().Be(0);
        }

        await using var verify = database.CreateDbContext();
        (await verify.Vouchers.AsNoTracking().SingleAsync(candidate => candidate.Id == voucherId))
            .UsedCount.Should().Be(0);
        (await verify.Bookings.AsNoTracking().SingleAsync(candidate => candidate.Id == bookingId))
            .DiscountAmount.Should().Be(100_000m);
        (await verify.TourSchedules.AsNoTracking().SingleAsync(candidate => candidate.Id == scheduleId))
            .ReservedCapacity.Should().Be(0);
        (await verify.VoucherRedemptions.CountAsync(candidate => candidate.BookingId == bookingId))
            .Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Create_WithTwoSimultaneousRequestsForTheLastSeat_OnSqlServer_OnlyOneSucceeds()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (travelerId, scheduleId) = await SeedBookingScheduleAsync(database, totalCapacity: 1);

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Result<CreateTourBookingResponse>> CreateAsync(string name)
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new CreateTourBookingCommandHandler(
                context,
                new TestCurrentUserService(travelerId),
                new FixedClock(),
                new PendingBookingExpiryProcessor(context, new FixedClock(), new TourBookingOptions()),
                new TourBookingOptions(),
                new PassThroughPiiProtector());

            var participant = new CreateTourBookingParticipantRequest(
                name, ParticipantType.Adult, null, null, null, null, null);
            return await handler.Handle(
                new CreateTourBookingCommand(scheduleId.ToString(), [participant], null, Guid.NewGuid()),
                CancellationToken.None);
        }

        var first = CreateAsync("Traveler A");
        var second = CreateAsync("Traveler B");
        startGate.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Count(r => r.IsFailure).Should().Be(1);

        await using var verify = database.CreateDbContext();
        (await verify.Bookings.CountAsync()).Should().Be(1);
        (await verify.TourSchedules.AsNoTracking().SingleAsync(s => s.Id == scheduleId)).ReservedCapacity
            .Should().Be(1);
    }

    private static async Task<(long TravelerId, long ScheduleId)> SeedBookingScheduleAsync(
        SqlServerTestDatabase database, int totalCapacity)
    {
        await using var context = database.CreateDbContext();
        var traveler = new User
        {
            Email = $"traveler-{Guid.NewGuid():N}@test.local",
            FullName = "SQL Booking Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var operatorUser = new User
        {
            Email = $"operator-{Guid.NewGuid():N}@test.local",
            FullName = "SQL Booking Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        context.Users.AddRange(traveler, operatorUser);
        await context.SaveChangesAsync();

        context.OperatorProfiles.Add(CreateApprovedOperatorProfile(operatorUser));
        await context.SaveChangesAsync();

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUser.Id);
        Set(tour, nameof(Tour.Title), "SQL booking tour");
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        context.Tours.Add(tour);
        await context.SaveChangesAsync();

        var schedule = TourSchedule.Create(
            Now.AddDays(7), Now.AddDays(8), null, totalCapacity: totalCapacity, createdAtUtc: Now,
            adultPrice: 1_000_000m);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();

        return (traveler.Id, schedule.Id);
    }

    private static async Task<(long BookingId, long ScheduleId)> SeedDueBookingAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var traveler = new User
        {
            Email = $"booking-expiry-{Guid.NewGuid():N}@example.test",
            FullName = "SQL Booking Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var operatorUser = new User
        {
            Email = $"booking-expiry-operator-{Guid.NewGuid():N}@example.test",
            FullName = "SQL Booking Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        context.Users.AddRange(traveler, operatorUser);
        await context.SaveChangesAsync();

        context.OperatorProfiles.Add(CreateApprovedOperatorProfile(operatorUser));
        await context.SaveChangesAsync();

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUser.Id);
        Set(tour, nameof(Tour.Title), "Expiry integration tour");
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        context.Tours.Add(tour);
        await context.SaveChangesAsync();

        var schedule = TourSchedule.Create(
            Now.AddDays(7), Now.AddDays(8), null, totalCapacity: 5, createdAtUtc: Now,
            registrationDeadlineAtUtc: Now,
            adultPrice: 1_000_000m);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        schedule.ReserveCapacity(1);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();

        var booking = Booking.Create(
            "BK-20261017-00001", traveler.Id, schedule.Id, null, 1_000_000m, 0m, 1_000_000m,
            [BookingParticipant.Create("Due traveler", ParticipantType.Adult, null, null, null, null, null)],
            Now.AddMinutes(-20), Now.AddMinutes(-5));
        context.Bookings.Add(booking);
        await context.SaveChangesAsync();
        return (booking.Id, schedule.Id);
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private static OperatorProfile CreateApprovedOperatorProfile(User operatorUser) => new()
    {
        UserId = operatorUser.Id,
        User = operatorUser,
        CompanyName = "SQL Booking Test Operator",
        TaxCode = $"SQL-BOOKING-TAX-{operatorUser.Id}",
        BusinessLicenseNo = $"SQL-BOOKING-LICENSE-{operatorUser.Id}",
        ApprovalStatus = OperatorApprovalStatus.Approved,
    };

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestCurrentUserService(long userId) : ICurrentUserService
    {
        public long? UserId => userId;
        public string? Email => $"user-{userId}@test.local";
        public string? Role => nameof(UserRole.Traveler);
    }

    private sealed class PassThroughPiiProtector : IPiiProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}