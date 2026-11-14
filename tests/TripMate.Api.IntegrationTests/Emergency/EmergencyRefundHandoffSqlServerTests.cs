using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Emergency;

/// <summary>
/// UC-70 (SRS UC-66) refund handoff on real SQL Server: the refunds, their cancellation operations,
/// the Administrator notice and the decision commit together, against the real FK, CHECK and unique
/// indexes, and a replay from a fresh context adds nothing.
/// </summary>
public sealed class EmergencyRefundHandoffSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private const string OperatorPassword = "OperatorPass123!";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CancelBeforeDeparture_RefundsVerifiedPaidBookings_AndReplayAddsNothing()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var commandId = Guid.NewGuid();

        EmergencyTourActionResponse first;
        await using (var db = database.CreateDbContext())
        {
            var impact = await new GetEmergencyTourImpactQueryHandler(db, OperatorUser(seed.OperatorId), new FixedClock(Now))
                .Handle(new GetEmergencyTourImpactQuery(seed.TourId, seed.ScheduleId), CancellationToken.None);
            impact.IsSuccess.Should().BeTrue("{0}", impact.ErrorMessage);
            var command = new CancelBeforeDepartureCommand(seed.TourId, seed.ScheduleId, commandId,
                impact.Value.ImpactVersion, EmergencyTourReason.WeatherEmergency,
                "Typhoon makes the departure unsafe.", "0900000000");
            var result = await Handler(db, seed.OperatorId).Handle(command, CancellationToken.None);
            result.IsSuccess.Should().BeTrue("{0}", result.ErrorMessage);
            first = result.Value;

            await using var replayDb = database.CreateDbContext();
            var replay = await Handler(replayDb, seed.OperatorId).Handle(command, CancellationToken.None);
            replay.IsSuccess.Should().BeTrue("{0}", replay.ErrorMessage);
            replay.Value.Should().Be(first);
        }

        first.RefundsSubmitted.Should().Be(2);
        await using var verify = database.CreateDbContext();
        var refunds = await verify.Refunds.AsNoTracking().OrderBy(refund => refund.BookingId).ToListAsync();
        refunds.Select(refund => (refund.BookingId, refund.Amount))
            .Should().Equal((seed.PaidBookingIds[0], 2_500_000m), (seed.PaidBookingIds[1], 1_200_000m));
        refunds.Should().OnlyContain(refund => refund.Status == RefundStatus.Pending
            && refund.InitiatedBy == RefundInitiator.TourOperator
            && refund.CancellationOperationId != null);
        (await verify.BookingCancellationOperations.AsNoTracking().CountAsync(operation =>
            operation.Actor == BookingCancellationActor.TourOperator
            && operation.ActorUserId == seed.OperatorId)).Should().Be(2);

        var adminNotices = await verify.Notifications.AsNoTracking()
            .Where(notice => notice.Type == Notification.EmergencyRefundReviewRequiredType)
            .ToListAsync();
        adminNotices.Should().Contain(notice => notice.UserId == seed.AdministratorId);
        adminNotices.Should().NotContain(notice => notice.UserId == seed.LockedAdministratorId);
        adminNotices.Should().OnlyContain(notice => notice.Channel == NotificationChannel.InApp
            && notice.RelatedEntityType == AuditEntityTypes.TourSchedule
            && notice.RelatedEntityId == seed.ScheduleId
            && notice.Body!.Contains("BK-UC70-UNVERIFIED")
            && !notice.Body.Contains("BK-UC70-UNPAID"));
        (await verify.Bookings.AsNoTracking().Where(booking => booking.TourScheduleId == seed.ScheduleId)
            .Select(booking => booking.PaymentStatus).ToListAsync())
            .Should().NotContain(PaymentStatus.Refunded, "UC-70 never settles a refund itself");
    }

    private static EmergencyTourCommandHandler Handler(IApplicationDbContext db, long operatorId) =>
        new(db, OperatorUser(operatorId), new FixedClock(Now), new PasswordHasherService(), new NoOpReviewNotifier());

    private sealed record Seed(long OperatorId, long AdministratorId, long LockedAdministratorId,
        long TourId, long ScheduleId, long[] PaidBookingIds);

    private static async Task<Seed> SeedAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var hasher = new PasswordHasherService();
        User NewUser(string email, UserRole role, AccountStatus status) => new()
        {
            Email = email,
            FullName = email,
            Role = role,
            Status = status,
            PasswordHash = hasher.Hash(OperatorPassword),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var operatorUser = NewUser("refund-operator@example.test", UserRole.TourOperator, AccountStatus.Active);
        var administrator = NewUser("refund-admin@example.test", UserRole.Administrator, AccountStatus.Active);
        var lockedAdministrator = NewUser("refund-locked-admin@example.test", UserRole.Administrator, AccountStatus.Locked);
        var travelers = Enumerable.Range(1, 4)
            .Select(index => NewUser($"refund-traveler-{index}@example.test", UserRole.Traveler, AccountStatus.Active))
            .ToArray();
        db.Users.AddRange([operatorUser, administrator, lockedAdministrator, .. travelers]);
        await db.SaveChangesAsync();
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            User = operatorUser,
            CompanyName = "UC70 Refund Tours",
            TaxCode = "UC70-REFUND-TAX",
            BusinessLicenseNo = "UC70-REFUND-LICENSE",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, operatorUser.Id);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        typeof(Tour).GetProperty(nameof(Tour.Title))!.SetValue(tour, "UC70 Refund Tour");
        typeof(Tour).GetProperty(nameof(Tour.BasePrice))!.SetValue(tour, 1_200_000m);
        typeof(Tour).GetProperty(nameof(Tour.DurationDays))!.SetValue(tour, 1);
        typeof(Tour).GetProperty(nameof(Tour.CreatedAtUtc))!.SetValue(tour, Now);
        typeof(Tour).GetProperty(nameof(Tour.UpdatedAtUtc))!.SetValue(tour, Now);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();

        var schedule = TourSchedule.Create(Now.AddDays(4), Now.AddDays(4).AddHours(8), "Da Nang", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();

        Booking NewBooking(string code, User traveler, decimal amount) => Booking.Create(code, traveler.Id,
            schedule.Id, null, amount, 0m, amount,
            [BookingParticipant.Create(traveler.FullName, ParticipantType.Adult, null, null, null, null, null)],
            Now.AddMinutes(-30), Now.AddMinutes(30));
        var paidA = NewBooking("BK-UC70-PAID-A", travelers[0], 2_500_000m);
        var paidB = NewBooking("BK-UC70-PAID-B", travelers[1], 1_200_000m);
        var unverified = NewBooking("BK-UC70-UNVERIFIED", travelers[2], 900_000m);
        var unpaid = NewBooking("BK-UC70-UNPAID", travelers[3], 700_000m);
        db.Bookings.AddRange(paidA, paidB, unverified, unpaid);
        schedule.ReserveCapacity(4);
        await db.SaveChangesAsync();

        long orderCode = 7000;
        foreach (Booking booking in new[] { paidA, paidB })
        {
            var payment = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount,
                orderCode++, Now.AddMinutes(-20));
            payment.MarkSuccess($"ref-{booking.Id}", null, Now.AddMinutes(-20), 0m);
            db.PaymentTransactions.Add(payment);
            booking.MarkPaid(Now.AddMinutes(-20)).Should().BeTrue();
        }

        // Paid on the booking, but its only captured payment does not match the total.
        var partial = PaymentTransaction.Create(unverified.Id, PaymentGateway.Mock, 400_000m,
            orderCode, Now.AddMinutes(-20));
        partial.MarkSuccess($"ref-{unverified.Id}", null, Now.AddMinutes(-20), 0m);
        db.PaymentTransactions.Add(partial);
        unverified.MarkPaid(Now.AddMinutes(-20)).Should().BeTrue();
        await db.SaveChangesAsync();

        return new Seed(operatorUser.Id, administrator.Id, lockedAdministrator.Id, tour.Id, schedule.Id,
            [paidA.Id, paidB.Id]);
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

    private sealed class NoOpReviewNotifier : IWeatherSafetyReviewNotifier
    {
        public Task NotifyChangedAsync(
            long operatorUserId,
            WeatherSafetyReviewChanged notification,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}