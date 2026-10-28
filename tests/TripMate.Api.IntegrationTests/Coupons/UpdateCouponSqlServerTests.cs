using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Cancel;
using TripMate.Application.Features.Coupons.Update;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Create;
using TripMate.Application.Features.Traveler.Bookings.Expiry;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Coupons;

public sealed class UpdateCouponSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRedemptionAndLimitReduction_PreservesUsageInvariantAndAtomicScope()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, usedCount: 1, includePendingBooking: false);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Result<CreateTourBookingResponse>> RedeemAsync()
        {
            await start.Task;
            await using var context = database.CreateDbContext();
            return await CreateBookingHandler(context, seed.TravelerId).Handle(
                BookingRequest(seed.ScheduleId, seed.Code), CancellationToken.None);
        }

        async Task<Result<TripMate.Application.Features.Coupons.Common.OperatorCouponResponse>> ReduceAsync()
        {
            await start.Task;
            await using var context = database.CreateDbContext();
            var handler = new UpdateCouponCommandHandler(
                context, new TestCurrentUserService(seed.OperatorId, UserRole.TourOperator), new FixedClock());
            return await handler.Handle(new UpdateCouponCommand(
                seed.VoucherId, "Reduced atomically", VoucherDiscountType.Percentage, 10m, 200_000m, 0m,
                1, 1, seed.ValidFromUtc, Now.AddDays(2), [seed.ReplacementTourId]), CancellationToken.None);
        }

        var redemption = RedeemAsync();
        var reduction = ReduceAsync();
        start.SetResult();
        await Task.WhenAll(redemption, reduction);

        redemption.Result.IsSuccess.Should().NotBe(reduction.Result.IsSuccess,
            "the last available slot can be retained by the update or consumed by the booking, never both");

        await using var verify = database.CreateDbContext();
        var voucher = await verify.Vouchers.AsNoTracking().Include(candidate => candidate.ApplicableTours)
            .SingleAsync(candidate => candidate.Id == seed.VoucherId);
        voucher.UsedCount.Should().BeLessThanOrEqualTo(voucher.UsageLimit!.Value);
        if (reduction.Result.IsSuccess)
        {
            voucher.Name.Should().Be("Reduced atomically");
            voucher.UsageLimit.Should().Be(1);
            voucher.ApplicableTours.Select(link => link.TourId).Should().Equal(seed.ReplacementTourId);
        }
        else
        {
            reduction.Result.ErrorCode.Should().Be("coupon.usage_conflict");
            voucher.Name.Should().Be("Original SQL coupon");
            voucher.UsageLimit.Should().Be(2);
            voucher.ApplicableTours.Select(link => link.TourId).Should().Equal(seed.TourId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UpdateTerms_PreservesExistingBookingSnapshotAndAppliesNewDiscountToLaterBooking()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, usedCount: 0, includePendingBooking: false);

        Result<CreateTourBookingResponse> earlierResult;
        await using (var earlierContext = database.CreateDbContext())
        {
            earlierResult = await CreateBookingHandler(earlierContext, seed.TravelerId).Handle(
                BookingRequest(seed.ScheduleId, seed.Code), CancellationToken.None);
        }
        earlierResult.IsSuccess.Should().BeTrue(earlierResult.ErrorMessage);
        earlierResult.Value.DiscountAmount.Should().Be(100_000m);
        earlierResult.Value.TotalAmount.Should().Be(900_000m);

        await using (var updateContext = database.CreateDbContext())
        {
            var update = await new UpdateCouponCommandHandler(
                updateContext,
                new TestCurrentUserService(seed.OperatorId, UserRole.TourOperator),
                new FixedClock()).Handle(new UpdateCouponCommand(
                    seed.VoucherId, "Updated SQL coupon", VoucherDiscountType.Percentage,
                    20m, 200_000m, 0m, 2, 1, seed.ValidFromUtc, Now.AddDays(2), [seed.TourId]),
                    CancellationToken.None);
            update.IsSuccess.Should().BeTrue(update.ErrorMessage);
        }

        long laterTravelerId;
        await using (var setup = database.CreateDbContext())
        {
            var laterTraveler = User("sql-coupon-later-traveler", UserRole.Traveler);
            setup.Users.Add(laterTraveler);
            await setup.SaveChangesAsync();
            laterTravelerId = laterTraveler.Id;
        }

        Result<CreateTourBookingResponse> laterResult;
        await using (var laterContext = database.CreateDbContext())
        {
            laterResult = await CreateBookingHandler(laterContext, laterTravelerId).Handle(
                BookingRequest(seed.ScheduleId, seed.Code), CancellationToken.None);
        }
        laterResult.IsSuccess.Should().BeTrue(laterResult.ErrorMessage);
        laterResult.Value.DiscountAmount.Should().Be(200_000m);
        laterResult.Value.TotalAmount.Should().Be(800_000m);

        await using var verify = database.CreateDbContext();
        long earlierBookingId = long.Parse(earlierResult.Value.BookingId, System.Globalization.CultureInfo.InvariantCulture);
        var persistedEarlier = await verify.Bookings.AsNoTracking()
            .SingleAsync(booking => booking.Id == earlierBookingId);
        persistedEarlier.DiscountAmount.Should().Be(100_000m);
        persistedEarlier.TotalAmount.Should().Be(900_000m);
        long laterBookingId = long.Parse(laterResult.Value.BookingId, System.Globalization.CultureInfo.InvariantCulture);
        var persistedLater = await verify.Bookings.AsNoTracking()
            .SingleAsync(booking => booking.Id == laterBookingId);
        persistedLater.DiscountAmount.Should().Be(200_000m);
        persistedLater.TotalAmount.Should().Be(800_000m);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentExpiryWorkers_ReleaseVoucherSlotExactlyOnce()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, usedCount: 0, includePendingBooking: true);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<PendingBookingExpirySweepResult> ExpireAsync()
        {
            await start.Task;
            await using var context = database.CreateDbContext();
            return await new PendingBookingExpiryProcessor(context, new FixedClock(), new TourBookingOptions())
                .ExpireDueBookingsAsync(CancellationToken.None);
        }

        var first = ExpireAsync();
        var second = ExpireAsync();
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Sum(result => result.ExpiredCount).Should().Be(1);
        await AssertReleasedOnceAsync(database, seed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentCancellationAndExpiry_ReleaseOnceAndAllowOnlyOneReplacementRedemption()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, usedCount: 0, includePendingBooking: true);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Result<CancelBookingResponse>> CancelAsync()
        {
            await start.Task;
            await using var context = database.CreateDbContext();
            var handler = new CancelBookingCommandHandler(
                context,
                new TestCurrentUserService(seed.TravelerId, UserRole.Traveler),
                new FixedClock(),
                new SqlServerBookingCancellationPersistenceErrorClassifier());
            return await handler.Handle(
                new CancelBookingCommand(seed.BookingId!.Value, Guid.NewGuid(), null, null), CancellationToken.None);
        }

        async Task<PendingBookingExpirySweepResult> ExpireAsync()
        {
            await start.Task;
            await using var context = database.CreateDbContext();
            return await new PendingBookingExpiryProcessor(context, new FixedClock(), new TourBookingOptions())
                .ExpireDueBookingsAsync(CancellationToken.None);
        }

        var cancellation = CancelAsync();
        var expiry = ExpireAsync();
        start.SetResult();
        await Task.WhenAll(cancellation, expiry);

        (cancellation.Result.IsSuccess ? 1 : 0).Should().Be(1 - expiry.Result.ExpiredCount);
        await AssertReleasedOnceAsync(database, seed);

        await using (var replacementContext = database.CreateDbContext())
        {
            var replacement = await CreateBookingHandler(replacementContext, seed.TravelerId).Handle(
                BookingRequest(seed.ScheduleId, seed.Code), CancellationToken.None);
            replacement.IsSuccess.Should().BeTrue(replacement.ErrorMessage);
        }

        await using var verify = database.CreateDbContext();
        var voucher = await verify.Vouchers.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.VoucherId);
        voucher.UsedCount.Should().Be(1);
        voucher.UsedCount.Should().BeLessThanOrEqualTo(voucher.UsageLimit!.Value);
    }

    private static async Task AssertReleasedOnceAsync(SqlServerTestDatabase database, Seed seed)
    {
        await using var verify = database.CreateDbContext();
        var voucher = await verify.Vouchers.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.VoucherId);
        voucher.UsedCount.Should().Be(0);
        (await verify.VoucherRedemptions.CountAsync(redemption => redemption.BookingId == seed.BookingId))
            .Should().Be(1);
        (await verify.TourSchedules.AsNoTracking().SingleAsync(schedule => schedule.Id == seed.ScheduleId))
            .ReservedCapacity.Should().Be(0);
        (await verify.Bookings.AsNoTracking().SingleAsync(booking => booking.Id == seed.BookingId)).Status
            .Should().BeOneOf(BookingStatus.Expired, BookingStatus.Cancelled);
    }

    private static CreateTourBookingCommand BookingRequest(long scheduleId, string code) => new(
        scheduleId.ToString(),
        [new CreateTourBookingParticipantRequest(
            "Replacement traveler", ParticipantType.Adult, null, null, null, null, null)],
        code,
        Guid.NewGuid());

    private static CreateTourBookingCommandHandler CreateBookingHandler(
        IApplicationDbContext context, long travelerId) => new(
        context,
        new TestCurrentUserService(travelerId, UserRole.Traveler),
        new FixedClock(),
        new PendingBookingExpiryProcessor(context, new FixedClock(), new TourBookingOptions()),
        new TourBookingOptions(),
        new PassThroughPiiProtector());

    private static async Task<Seed> SeedAsync(
        SqlServerTestDatabase database, int usedCount, bool includePendingBooking)
    {
        await using var context = database.CreateDbContext();
        var traveler = User("sql-coupon-traveler", UserRole.Traveler);
        var operatorUser = User("sql-coupon-operator", UserRole.TourOperator);
        context.Users.AddRange(traveler, operatorUser);
        await context.SaveChangesAsync();

        context.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            User = operatorUser,
            CompanyName = "SQL Coupon Operator",
            TaxCode = $"SQL-COUPON-TAX-{operatorUser.Id}",
            BusinessLicenseNo = $"SQL-COUPON-LICENSE-{operatorUser.Id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });

        var tour = CreateTour(operatorUser.Id, "SQL coupon tour");
        var replacementTour = CreateTour(operatorUser.Id, "SQL replacement tour");
        context.Tours.AddRange(tour, replacementTour);
        await context.SaveChangesAsync();

        var schedule = TourSchedule.Create(
            Now.AddDays(7), Now.AddDays(8), null, totalCapacity: 5, createdAtUtc: Now,
            registrationDeadlineAtUtc: Now.AddDays(6), adultPrice: 1_000_000m);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        context.TourSchedules.Add(schedule);

        var validFrom = Now.AddDays(-1);
        var voucher = Voucher.Create(
            operatorUser.Id, "SQLRACE10", "Original SQL coupon", VoucherDiscountType.Percentage,
            10m, 200_000m, 0m, 2, 1, validFrom, Now.AddDays(2), [tour], Now.AddDays(-2));
        for (var index = 0; index < usedCount; index++) voucher.IncrementUsage();
        context.Vouchers.Add(voucher);
        await context.SaveChangesAsync();

        long? bookingId = null;
        if (includePendingBooking)
        {
            schedule.ReserveCapacity(1);
            var booking = Booking.Create(
                "BK-20261026-00001", traveler.Id, schedule.Id, voucher.Id,
                1_000_000m, 100_000m, 900_000m,
                [BookingParticipant.Create(
                    "Pending traveler", ParticipantType.Adult, null, null, null, null, null)],
                Now.AddMinutes(-30), Now.AddMinutes(-1));
            context.Bookings.Add(booking);
            voucher.IncrementUsage();
            context.VoucherRedemptions.Add(VoucherRedemption.Create(voucher, booking, 100_000m, Now.AddMinutes(-30)));
            await context.SaveChangesAsync();
            bookingId = booking.Id;
        }

        return new Seed(
            operatorUser.Id, traveler.Id, tour.Id, replacementTour.Id, schedule.Id, voucher.Id,
            bookingId, voucher.Code, validFrom);
    }

    private static User User(string prefix, UserRole role) => new()
    {
        Email = $"{prefix}-{Guid.NewGuid():N}@test.local",
        FullName = prefix,
        Role = role,
        Status = AccountStatus.Active,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    private static Tour CreateTour(long operatorId, string title)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorId);
        Set(tour, nameof(Tour.Title), title);
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        return tour;
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed record Seed(
        long OperatorId,
        long TravelerId,
        long TourId,
        long ReplacementTourId,
        long ScheduleId,
        long VoucherId,
        long? BookingId,
        string Code,
        DateTimeOffset ValidFromUtc);

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestCurrentUserService(long userId, UserRole role) : ICurrentUserService
    {
        public long? UserId => userId;
        public string? Email => $"user-{userId}@test.local";
        public string? Role => role.ToString();
    }

    private sealed class PassThroughPiiProtector : IPiiProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}