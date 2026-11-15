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
    // own start hour (DailyPayoutClock.RunStartHour) so the hourly retry grid (11:00/12:00/13:00
    // VN) lines up without rounding surprises.
    private static readonly DateTimeOffset RunTimeUtc = new(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);

    private static DailyPayoutRunProcessor CreateProcessor(
        ApplicationDbContext context, IPayoutTransferGateway gateway, IDateTimeProvider clock) =>
        new(context, new DailyPayoutEligibilityReader(context),
            new DailyPayoutRetrySweepProcessor(context, gateway, clock, new DailyPayoutEligibilityReader(context)), clock);

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
            var processor = CreateProcessor(context, gateway, clock);
            (await processor.RunIfDueAsync(CancellationToken.None)).Should().Be(DailyPayoutRunOutcome.Ran);
        }

        await using var verify = database.CreateDbContext();
        (await verify.PayoutRuns.CountAsync()).Should().Be(1);
        var batch = await verify.PayoutBatches.AsNoTracking().SingleAsync();
        batch.TourOperatorUserId.Should().Be(operatorUserId);
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.TotalAmount.Should().Be(700_000m); // Advance = 70% of 1,000,000 PaidAmount (PD-3)
        var settlement = await verify.PayoutSettlements.AsNoTracking().SingleAsync();
        settlement.BookingId.Should().Be(bookingId);
        settlement.Kind.Should().Be(PayoutSettlementKind.Advance);
        settlement.PayoutAmount.Should().Be(700_000m);
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
            var processor = CreateProcessor(context, gateway, new MutableClock { UtcNow = RunTimeUtc });
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
            var processor = CreateProcessor(context, gateway, new MutableClock { UtcNow = RunTimeUtc.AddDays(1) });
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
            var processor = CreateProcessor(context, gateway, clock);
            (await processor.RunIfDueAsync(CancellationToken.None)).Should().Be(DailyPayoutRunOutcome.Ran);
        }

        // A later poll the same day (whether the regular scheduler or another restart) must not
        // create a second run/batch for the same RunDate (PD-11).
        clock.UtcNow = RunTimeUtc.AddHours(6);
        await using (var context = database.CreateDbContext())
        {
            var processor = CreateProcessor(context, gateway, clock);
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
            var processor = CreateProcessor(context, timeoutGateway, clock);
            await processor.RunIfDueAsync(CancellationToken.None);
        }

        await using (var afterRun = database.CreateDbContext())
        {
            (await afterRun.PayoutBatches.AsNoTracking().SingleAsync()).Status.Should().Be(PayoutSettlementStatus.Scheduled);
        }

        // One hour later, the sweep is due. The gateway now reports the earlier transfer actually
        // went through (PD-7a) — the batch must settle from that lookup alone, never resending.
        clock.UtcNow = RunTimeUtc.AddHours(1);
        var succeededGateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));
        await using (var context = database.CreateDbContext())
        {
            var sweep = new DailyPayoutRetrySweepProcessor(context, succeededGateway, clock, new DailyPayoutEligibilityReader(context));
            (await sweep.SweepAsync(CancellationToken.None)).Should().Be(1);
        }

        await using var verify = database.CreateDbContext();
        var batch = await verify.PayoutBatches.AsNoTracking().SingleAsync();
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.AttemptCount.Should().Be(1, "the lookup alone settled it — no second transfer was ever sent");
        (await verify.PayoutSettlementAttempts.CountAsync()).Should().Be(1);
        // Regression: the sweep's own PayoutBatches query must include Settlements, or
        // PayoutBatch.MarkSettled's cascade (PD-10) silently no-ops on an empty navigation
        // collection and the per-booking row stays stuck at Processing forever (UC-72 would
        // never reflect the real outcome).
        (await verify.PayoutSettlements.AsNoTracking().SingleAsync()).Status.Should().Be(PayoutSettlementStatus.Settled);
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
            var processor = CreateProcessor(context, timeoutGateway, clock);
            await processor.RunIfDueAsync(CancellationToken.None);
        }

        // Post-send reconciliation (PD-17): the gateway now reports the earlier transfer was
        // rejected by the bank. Treated as permanent — no further retry is attempted.
        clock.UtcNow = RunTimeUtc.AddHours(1);
        var rejectedGateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Rejected));
        await using (var context = database.CreateDbContext())
        {
            var sweep = new DailyPayoutRetrySweepProcessor(context, rejectedGateway, clock, new DailyPayoutEligibilityReader(context));
            await sweep.SweepAsync(CancellationToken.None);
        }

        await using var verify = database.CreateDbContext();
        var batch = await verify.PayoutBatches.AsNoTracking().SingleAsync();
        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.AttemptCount.Should().Be(1, "a permanent rejection from the lookup stops retries without another send");
        // Regression (see the sibling test above): the settlement must observe the same failure.
        (await verify.PayoutSettlements.AsNoTracking().SingleAsync()).Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RetrySweep_NaturalHourlySequenceFromTenAm_SendsAllFourAttemptsThenFailsOnTheLastOne()
    {
        // A run starting exactly at 10:00 VN, swept hourly by a real scheduler rather than by
        // seeding AttemptCount directly. No same-day cutoff in v7: only the 3-retry budget
        // (BR-145) bounds this. The 4th (last allowed) attempt, at the 13:00 VN sweep, resolves
        // to SettlementFailed immediately within that same sweep (SRS 3.10.5, 3.b1) — no further
        // waiting cycle is needed.
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorUserId = await SeedOperatorAsync(database, "run-cutoff");
        var scheduleId = await SeedScheduleAsync(database, operatorUserId);
        await SeedPaidBookingAsync(database, scheduleId, totalAmount: 1_000_000m, succeededAtUtc: RunTimeUtc.AddHours(-25));

        var clock = new MutableClock { UtcNow = RunTimeUtc };
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout));

        await using (var context = database.CreateDbContext())
        {
            var processor = CreateProcessor(context, gateway, clock);
            await processor.RunIfDueAsync(CancellationToken.None); // attempt 1 @ 10:00 VN
        }

        await using (var afterFirstAttempt = database.CreateDbContext())
        {
            var batch = await afterFirstAttempt.PayoutBatches.AsNoTracking().SingleAsync();
            batch.Status.Should().Be(PayoutSettlementStatus.Scheduled, "rests as Scheduled between attempts, not Processing");
            batch.AttemptCount.Should().Be(1);
        }

        foreach (var hour in new[] { 1, 2 }) // sweeps @ 11:00, 12:00 VN -> attempts 2, 3
        {
            clock.UtcNow = RunTimeUtc.AddHours(hour);
            await using var context = database.CreateDbContext();
            var sweep = new DailyPayoutRetrySweepProcessor(context, gateway, clock, new DailyPayoutEligibilityReader(context));
            await sweep.SweepAsync(CancellationToken.None);
        }

        await using (var afterThirdAttempt = database.CreateDbContext())
        {
            var batch = await afterThirdAttempt.PayoutBatches.AsNoTracking().SingleAsync();
            batch.Status.Should().Be(PayoutSettlementStatus.Scheduled, "the 3-retry budget (BR-145) is not yet exhausted");
            batch.AttemptCount.Should().Be(3);
        }

        clock.UtcNow = RunTimeUtc.AddHours(3); // sweep @ 13:00 VN -> 4th (last allowed) attempt, fails
        await using (var context = database.CreateDbContext())
        {
            var sweep = new DailyPayoutRetrySweepProcessor(context, gateway, clock, new DailyPayoutEligibilityReader(context));
            await sweep.SweepAsync(CancellationToken.None);
        }

        await using var verify = database.CreateDbContext();
        var finalBatch = await verify.PayoutBatches.AsNoTracking().SingleAsync();
        finalBatch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        finalBatch.AttemptCount.Should().Be(4, "1 initial + 3 retries, BR-145");
        (await verify.PayoutSettlementAttempts.CountAsync()).Should().Be(4);
        // Regression: budget-exhaustion also goes through MarkFailed's cascade (PD-10) — must not
        // leave the settlement stuck at Scheduled (see the two sibling tests above).
        (await verify.PayoutSettlements.AsNoTracking().SingleAsync()).Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DailyRun_WithTourOperatorLocked_SchedulesTheBatchButDefersItsTransferUntilUnlocked()
    {
        // SRS 3.10.5 (BR-160): scheduling does not check the lock — the batch/settlement is
        // still created on time; only the transfer is deferred, and the SAME batch (never
        // recreated, PD-11) is transferred once the operator is unlocked and swept again.
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
            var processor = CreateProcessor(context, gateway, clock);
            await processor.RunIfDueAsync(CancellationToken.None);
        }

        long scheduledBatchId;
        await using (var whileLocked = database.CreateDbContext())
        {
            var batch = await whileLocked.PayoutBatches.AsNoTracking().SingleAsync();
            batch.Status.Should().Be(PayoutSettlementStatus.Scheduled, "BR-160: scheduled on time, but not transferred while Locked");
            batch.AttemptCount.Should().Be(0);
            scheduledBatchId = batch.Id;
            (await whileLocked.PayoutSettlements.AsNoTracking().SingleAsync()).Status.Should().Be(PayoutSettlementStatus.Scheduled);
        }

        await using (var unlockContext = database.CreateDbContext())
        {
            var operatorUser = await unlockContext.Users.SingleAsync(u => u.Id == operatorUserId);
            operatorUser.Status = AccountStatus.Active;
            await unlockContext.SaveChangesAsync();
        }

        // The sweep after the account is unlocked transfers the same Scheduled batch.
        var nextDay = RunTimeUtc.AddDays(1);
        await using (var context = database.CreateDbContext())
        {
            var sweep = new DailyPayoutRetrySweepProcessor(context, gateway, new MutableClock { UtcNow = nextDay }, new DailyPayoutEligibilityReader(context));
            (await sweep.SweepAsync(CancellationToken.None)).Should().Be(1);
        }

        await using var verify = database.CreateDbContext();
        var finalBatch = await verify.PayoutBatches.AsNoTracking().SingleAsync();
        finalBatch.Id.Should().Be(scheduledBatchId, "the same batch, never recreated (PD-11)");
        finalBatch.TourOperatorUserId.Should().Be(operatorUserId);
        finalBatch.Status.Should().Be(PayoutSettlementStatus.Settled);
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
            // SRS 3.10.5 step 1: a transfer requires a complete beneficiary bank account (MSG146
            // otherwise) — set by default so these tests exercise a real simulated transfer.
            BankName = "Test Bank",
            AccountHolderName = "Operator Holder",
            AccountNumberMasked = "****1234",
            AccountNumberProtected = "protected-test-value",
            BankAccountUpdatedAtUtc = RunTimeUtc,
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