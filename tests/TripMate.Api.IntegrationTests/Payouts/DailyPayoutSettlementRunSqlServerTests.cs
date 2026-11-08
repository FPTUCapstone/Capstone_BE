using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Payouts;

/// <summary>
/// DAILY-PAYOUT-TRANSFER Task 11: the run/retry-sweep processors against a real SQL Server
/// database (real transactions, unique constraints, FK-backed Tour/TourSchedule), which the
/// Application.UnitTests suite's InMemory provider cannot exercise. Each test seeds its own
/// operator/booking so the cases are independent of each other and of execution order.
/// </summary>
public sealed class DailyPayoutSettlementRunSqlServerTests
{
    // 2026-11-09 10:00 Vietnam time (UTC+7) = 03:00 UTC. Chosen to land exactly on the run's
    // own start hour so the hourly retry grid (11:00/12:00/13:00 VN) lines up with
    // DailyPayoutClock's RunStartHour/RetryCutoffHour boundaries without rounding surprises.
    private static readonly DateTimeOffset RunTimeUtc = new(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DailyRun_OnSqlServer_CreatesOneBatchAndSettlesItSuccessfully()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorUserId = await SeedOperatorAsync(database, "run-ok");
        var scheduleId = await SeedScheduleAsync(database, operatorUserId);
        var bookingId = await SeedPaidBookingAsync(database, scheduleId, totalAmount: 1_000_000m,
            succeededAtUtc: RunTimeUtc.AddHours(-25));

        var clock = new MutableClock { UtcNow = RunTimeUtc };
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        await using (var context = database.CreateDbContext())
        {
            var processor = new DailyPayoutRunProcessor(context, new DailyPayoutEligibilityReader(context), gateway, clock);
            (await processor.RunIfDueAsync(CancellationToken.None)).Should().Be(DailyPayoutRunOutcome.Ran);
        }

        await using var verify = database.CreateDbContext();
        (await verify.PayoutRuns.CountAsync()).Should().Be(1);
        var batch = await verify.PayoutBatches.AsNoTracking().SingleAsync();
        batch.TourOperatorUserId.Should().Be(operatorUserId);
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.TotalAmount.Should().Be(900_000m); // 1,000,000 gross - 10% commission (BR-140)
        var settlement = await verify.PayoutSettlements.AsNoTracking().SingleAsync();
        settlement.BookingId.Should().Be(bookingId);
        settlement.PayoutAmount.Should().Be(900_000m);
        settlement.Status.Should().Be(PayoutSettlementStatus.Settled);
        (await verify.PayoutSettlementAttempts.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DailyRun_BookingStillInsideItsAwaitingWindow_IsExcludedUntilTheWindowEnds()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorUserId = await SeedOperatorAsync(database, "run-window");
        var scheduleId = await SeedScheduleAsync(database, operatorUserId);
        var bookingId = await SeedPaidBookingAsync(database, scheduleId, totalAmount: 1_000_000m,
            succeededAtUtc: RunTimeUtc.AddHours(-2)); // paid 2h ago — still inside BR-141's 24h window

        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        await using (var context = database.CreateDbContext())
        {
            var processor = new DailyPayoutRunProcessor(context, new DailyPayoutEligibilityReader(context), gateway,
                new MutableClock { UtcNow = RunTimeUtc });
            (await processor.RunIfDueAsync(CancellationToken.None)).Should().Be(DailyPayoutRunOutcome.Ran);
        }

        await using (var whileInWindow = database.CreateDbContext())
        {
            (await whileInWindow.PayoutBatches.CountAsync()).Should().Be(0, "BR-141: still inside the 24-hour Awaiting Window");
            (await whileInWindow.PayoutSettlements.CountAsync()).Should().Be(0);
        }

        // The next day's run: the window has now ended, so the booking is settled normally.
        await using (var context = database.CreateDbContext())
        {
            var processor = new DailyPayoutRunProcessor(context, new DailyPayoutEligibilityReader(context), gateway,
                new MutableClock { UtcNow = RunTimeUtc.AddDays(1) });
            (await processor.RunIfDueAsync(CancellationToken.None)).Should().Be(DailyPayoutRunOutcome.Ran);
        }

        await using var verify = database.CreateDbContext();
        var settlement = await verify.PayoutSettlements.AsNoTracking().SingleAsync();
        settlement.BookingId.Should().Be(bookingId);
        settlement.Status.Should().Be(PayoutSettlementStatus.Settled);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DailyRun_PolledAgainLaterTheSameDay_IsANoOp_ButAfterRestartItStillCatchesUp()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorUserId = await SeedOperatorAsync(database, "run-twice");
        var scheduleId = await SeedScheduleAsync(database, operatorUserId);
        await SeedPaidBookingAsync(database, scheduleId, totalAmount: 500_000m, succeededAtUtc: RunTimeUtc.AddHours(-25));

        // Simulates a restart: the first poll to notice "past 10:00, no run yet today" happens
        // at 14:00 VN instead of 10:00 VN (PD-16), not exactly at the run's own start hour.
        var clock = new MutableClock { UtcNow = RunTimeUtc.AddHours(4) };
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        await using (var context = database.CreateDbContext())
        {
            var processor = new DailyPayoutRunProcessor(context, new DailyPayoutEligibilityReader(context), gateway, clock);
            (await processor.RunIfDueAsync(CancellationToken.None)).Should().Be(DailyPayoutRunOutcome.Ran);
        }

        // A later poll the same day (whether the regular scheduler or another restart) must not
        // create a second run/batch for the same RunDate (PD-11).
        clock.UtcNow = RunTimeUtc.AddHours(6);
        await using (var context = database.CreateDbContext())
        {
            var processor = new DailyPayoutRunProcessor(context, new DailyPayoutEligibilityReader(context), gateway, clock);
            (await processor.RunIfDueAsync(CancellationToken.None)).Should().Be(DailyPayoutRunOutcome.AlreadyRan);
        }

        await using var verify = database.CreateDbContext();
        (await verify.PayoutRuns.CountAsync()).Should().Be(1);
        (await verify.PayoutBatches.CountAsync()).Should().Be(1);
        (await verify.PayoutSettlements.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RetrySweep_UnresolvedSendThenLookupReportsSucceeded_SettlesWithoutASecondSend()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorUserId = await SeedOperatorAsync(database, "run-timeout");
        var scheduleId = await SeedScheduleAsync(database, operatorUserId);
        await SeedPaidBookingAsync(database, scheduleId, totalAmount: 1_000_000m, succeededAtUtc: RunTimeUtc.AddHours(-25));

        var clock = new MutableClock { UtcNow = RunTimeUtc };
        var timeoutGateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout));

        await using (var context = database.CreateDbContext())
        {
            var processor = new DailyPayoutRunProcessor(context, new DailyPayoutEligibilityReader(context), timeoutGateway, clock);
            await processor.RunIfDueAsync(CancellationToken.None);
        }

        await using (var afterRun = database.CreateDbContext())
        {
            (await afterRun.PayoutBatches.AsNoTracking().SingleAsync()).Status.Should().Be(PayoutSettlementStatus.Processing);
        }

        // One hour later, the sweep is due. The gateway now reports the earlier transfer actually
        // went through (PD-7a) — the batch must settle from that lookup alone, never resending.
        clock.UtcNow = RunTimeUtc.AddHours(1);
        var succeededGateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));
        await using (var context = database.CreateDbContext())
        {
            var sweep = new DailyPayoutRetrySweepProcessor(context, succeededGateway, clock);
            (await sweep.SweepAsync(CancellationToken.None)).Should().Be(1);
        }

        await using var verify = database.CreateDbContext();
        var batch = await verify.PayoutBatches.AsNoTracking().SingleAsync();
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.AttemptCount.Should().Be(1, "the lookup alone settled it — no second transfer was ever sent");
        (await verify.PayoutSettlementAttempts.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RetrySweep_LookupReportsRejectedAfterASend_MarksSettlementFailedImmediately()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorUserId = await SeedOperatorAsync(database, "run-rejected");
        var scheduleId = await SeedScheduleAsync(database, operatorUserId);
        await SeedPaidBookingAsync(database, scheduleId, totalAmount: 1_000_000m, succeededAtUtc: RunTimeUtc.AddHours(-25));

        var clock = new MutableClock { UtcNow = RunTimeUtc };
        var timeoutGateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout));

        await using (var context = database.CreateDbContext())
        {
            var processor = new DailyPayoutRunProcessor(context, new DailyPayoutEligibilityReader(context), timeoutGateway, clock);
            await processor.RunIfDueAsync(CancellationToken.None);
        }

        // Post-send reconciliation (PD-17): the gateway now reports the earlier transfer was
        // rejected by the bank. Treated as permanent — no further retry is attempted.
        clock.UtcNow = RunTimeUtc.AddHours(1);
        var rejectedGateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Rejected));
        await using (var context = database.CreateDbContext())
        {
            var sweep = new DailyPayoutRetrySweepProcessor(context, rejectedGateway, clock);
            await sweep.SweepAsync(CancellationToken.None);
        }

        await using var verify = database.CreateDbContext();
        var batch = await verify.PayoutBatches.AsNoTracking().SingleAsync();
        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.AttemptCount.Should().Be(1, "a permanent rejection from the lookup stops retries without another send");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RetrySweep_NaturalHourlySequenceFromTenAm_SendsAllThreeRetriesThenFailsOnBudget()
    {
        // A run starting exactly at 10:00 VN, swept hourly by a real scheduler rather than by
        // seeding AttemptCount directly: the 13:00 VN sweep is exactly on IsPastRetryCutoffFor's
        // boundary, not past it, so it still sends the 3rd (last) retry; only the 14:00 sweep,
        // once the budget of 3 retries is exhausted, fails the batch.
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorUserId = await SeedOperatorAsync(database, "run-cutoff");
        var scheduleId = await SeedScheduleAsync(database, operatorUserId);
        await SeedPaidBookingAsync(database, scheduleId, totalAmount: 1_000_000m, succeededAtUtc: RunTimeUtc.AddHours(-25));

        var clock = new MutableClock { UtcNow = RunTimeUtc };
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout));

        await using (var context = database.CreateDbContext())
        {
            var processor = new DailyPayoutRunProcessor(context, new DailyPayoutEligibilityReader(context), gateway, clock);
            await processor.RunIfDueAsync(CancellationToken.None); // attempt 1 @ 10:00 VN
        }

        foreach (var hour in new[] { 1, 2, 3 }) // sweeps @ 11:00, 12:00, 13:00 VN -> retries 2, 3, 4
        {
            clock.UtcNow = RunTimeUtc.AddHours(hour);
            await using var context = database.CreateDbContext();
            var sweep = new DailyPayoutRetrySweepProcessor(context, gateway, clock);
            await sweep.SweepAsync(CancellationToken.None);
        }

        await using (var afterThirdRetry = database.CreateDbContext())
        {
            var batch = await afterThirdRetry.PayoutBatches.AsNoTracking().SingleAsync();
            batch.Status.Should().Be(PayoutSettlementStatus.Processing, "the 13:00 retry is exactly on the cutoff, not past it");
            batch.AttemptCount.Should().Be(4);
        }

        clock.UtcNow = RunTimeUtc.AddHours(4); // sweep @ 14:00 VN -> budget exhausted
        await using (var context = database.CreateDbContext())
        {
            var sweep = new DailyPayoutRetrySweepProcessor(context, gateway, clock);
            await sweep.SweepAsync(CancellationToken.None);
        }

        await using var verify = database.CreateDbContext();
        var finalBatch = await verify.PayoutBatches.AsNoTracking().SingleAsync();
        finalBatch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        finalBatch.AttemptCount.Should().Be(4, "exhausting the budget must not count as another attempt");
        (await verify.PayoutSettlementAttempts.CountAsync()).Should().Be(4);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DailyRun_WithTourOperatorLocked_ExcludesItsBookingUntilUnlocked()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorUserId = await SeedOperatorAsync(database, "run-locked");
        var scheduleId = await SeedScheduleAsync(database, operatorUserId);
        await SeedPaidBookingAsync(database, scheduleId, totalAmount: 1_000_000m, succeededAtUtc: RunTimeUtc.AddHours(-25));

        await using (var lockContext = database.CreateDbContext())
        {
            var operatorUser = await lockContext.Users.SingleAsync(u => u.Id == operatorUserId);
            operatorUser.Status = AccountStatus.Locked;
            await lockContext.SaveChangesAsync();
        }

        var clock = new MutableClock { UtcNow = RunTimeUtc };
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        await using (var context = database.CreateDbContext())
        {
            var processor = new DailyPayoutRunProcessor(context, new DailyPayoutEligibilityReader(context), gateway, clock);
            await processor.RunIfDueAsync(CancellationToken.None);
        }

        await using (var whileLocked = database.CreateDbContext())
        {
            (await whileLocked.PayoutBatches.CountAsync()).Should().Be(0, "BR-160: a locked operator's booking is not settled");
            (await whileLocked.PayoutSettlements.CountAsync()).Should().Be(0);
        }

        await using (var unlockContext = database.CreateDbContext())
        {
            var operatorUser = await unlockContext.Users.SingleAsync(u => u.Id == operatorUserId);
            operatorUser.Status = AccountStatus.Active;
            await unlockContext.SaveChangesAsync();
        }

        // Next day's run: the booking is now eligible and settles normally.
        clock.UtcNow = RunTimeUtc.AddDays(1);
        await using (var context = database.CreateDbContext())
        {
            var processor = new DailyPayoutRunProcessor(context, new DailyPayoutEligibilityReader(context), gateway, clock);
            (await processor.RunIfDueAsync(CancellationToken.None)).Should().Be(DailyPayoutRunOutcome.Ran);
        }

        await using var verify = database.CreateDbContext();
        var batch = await verify.PayoutBatches.AsNoTracking().SingleAsync();
        batch.TourOperatorUserId.Should().Be(operatorUserId);
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
    }

    private static async Task<long> SeedOperatorAsync(SqlServerTestDatabase database, string tag)
    {
        await using var context = database.CreateDbContext();
        var operatorUser = new User
        {
            Email = $"payout-{tag}-{Guid.NewGuid():N}@test.local",
            FullName = "Payout Test Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = RunTimeUtc,
            UpdatedAtUtc = RunTimeUtc,
        };
        context.Users.Add(operatorUser);
        await context.SaveChangesAsync();

        context.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            User = operatorUser,
            CompanyName = "Payout Test Co",
            TaxCode = $"PAYOUT-TAX-{operatorUser.Id}",
            BusinessLicenseNo = $"PAYOUT-LIC-{operatorUser.Id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CommissionRate = 10m,
        });
        await context.SaveChangesAsync();

        return operatorUser.Id;
    }

    private static async Task<long> SeedScheduleAsync(SqlServerTestDatabase database, long operatorUserId)
    {
        await using var context = database.CreateDbContext();

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Title), "Payout integration tour");
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);

        var (stopOneId, stopTwoId) = await SeedBookableStopsAsync(context);
        var itemsField = typeof(Tour).GetField("_itineraryItems", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<TourItineraryItem>)itemsField.GetValue(tour)!).AddRange(
        [
            TourItineraryItem.Create(1, 1, stopOneId, new TimeOnly(9, 0), 60, null),
            TourItineraryItem.Create(1, 2, stopTwoId, new TimeOnly(11, 0), 45, null),
        ]);
        context.Tours.Add(tour);
        await context.SaveChangesAsync();

        var schedule = TourSchedule.Create(
            RunTimeUtc.AddDays(30), RunTimeUtc.AddDays(31), null, totalCapacity: 20, createdAtUtc: RunTimeUtc.AddDays(-5));
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();

        return schedule.Id;
    }

    private static async Task<long> SeedPaidBookingAsync(
        SqlServerTestDatabase database, long scheduleId, decimal totalAmount, DateTimeOffset succeededAtUtc)
    {
        await using var context = database.CreateDbContext();

        var travelerId = (await context.Users.AddAsync(new User
        {
            Email = $"payout-traveler-{Guid.NewGuid():N}@test.local",
            FullName = "Payout Test Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = succeededAtUtc,
            UpdatedAtUtc = succeededAtUtc,
        })).Entity;
        await context.SaveChangesAsync();

        var bookedAt = succeededAtUtc.AddMinutes(-60);
        var booking = Booking.Create(
            bookingCode: $"BK-DP-{Guid.NewGuid():N}"[..20], travelerUserId: travelerId.Id, tourScheduleId: scheduleId,
            voucherId: null, unitPrice: totalAmount, discountAmount: 0m, totalAmount: totalAmount,
            participants: [BookingParticipant.Create("Adult", ParticipantType.Adult, null, null, null, null, null)],
            bookedAtUtc: bookedAt, paymentExpiresAtUtc: succeededAtUtc.AddMinutes(30));
        booking.MarkPaid(succeededAtUtc);
        context.Bookings.Add(booking);
        await context.SaveChangesAsync();

        var transaction = PaymentTransaction.Create(
            bookingId: booking.Id, gateway: PaymentGateway.Mock, amount: totalAmount,
            orderCode: booking.Id, createdAtUtc: bookedAt);
        transaction.MarkSuccess(gatewayRef: null, rawResponse: null, nowUtc: succeededAtUtc, commissionRateApplied: 10m);
        context.PaymentTransactions.Add(transaction);
        await context.SaveChangesAsync();

        return booking.Id;
    }

    // UC-70 bookability: a sellable tour template needs real POI-backed stops (the SQL FK
    // enforces them), same as TravelerTourBookingExpirySqlServerTests.
    private static async Task<(long StopOneId, long StopTwoId)> SeedBookableStopsAsync(ApplicationDbContext context)
    {
        var category = PoiCategory.Create("Beach", null);
        context.PoiCategories.Add(category);
        await context.SaveChangesAsync();

        var poiOne = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        Set(poiOne, nameof(PointOfInterest.Name), "Payout Stop One");
        Set(poiOne, nameof(PointOfInterest.Latitude), 16.0m);
        Set(poiOne, nameof(PointOfInterest.Longitude), 108.0m);
        Set(poiOne, nameof(PointOfInterest.CategoryId), category.Id);
        var poiTwo = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        Set(poiTwo, nameof(PointOfInterest.Name), "Payout Stop Two");
        Set(poiTwo, nameof(PointOfInterest.Latitude), 16.0m);
        Set(poiTwo, nameof(PointOfInterest.Longitude), 108.2m);
        Set(poiTwo, nameof(PointOfInterest.CategoryId), category.Id);
        context.PointsOfInterest.AddRange(poiOne, poiTwo);
        await context.SaveChangesAsync();

        return (poiOne.Id, poiTwo.Id);
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed class MutableClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    /// <summary>Always returns the same outcome for both Send and Lookup, for every reference.</summary>
    private sealed class FixedOutcomeScenario(PayoutTransferOutcome outcome) : IPayoutTransferScenario
    {
        public PayoutTransferResult Send(string gatewayReference, long tourOperatorUserId, decimal amount) =>
            new(outcome, gatewayReference, outcome == PayoutTransferOutcome.Succeeded ? null : outcome.ToString());

        public PayoutTransferResult Lookup(string gatewayReference) =>
            new(outcome, gatewayReference, outcome == PayoutTransferOutcome.Succeeded ? null : outcome.ToString());
    }
}
