using FluentAssertions;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Application.Features.Payouts.DailySettlement.Admin;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Payouts;

// DAILY-PAYOUT-TRANSFER, Task 6/7 (spec v6, PD-15, BR-144, BR-188, BR-189, BR-169, BR-175, PD-9A).
public sealed class DailyPayoutDebtAndAutoLockTests
{
    // 10:30 Vietnam time on 2026-11-09 -> 03:30 UTC.
    private static readonly DateTimeOffset AtRunTime = new(2026, 11, 9, 3, 30, 0, TimeSpan.Zero);

    private static DailyPayoutRunProcessor CreateProcessor(
        TestDbContext db, IPayoutTransferGateway gateway, DateTimeOffset nowUtc)
    {
        var clock = new PayoutTestHelpers.FixedClock(nowUtc);
        var retryProcessor = new DailyPayoutRetrySweepProcessor(db, gateway, clock);
        return new(db, new DailyPayoutEligibilityReader(db), retryProcessor, clock);
    }

    private static async Task SeedDebtAsync(TestDbContext db, long operatorId, decimal amount, DateTimeOffset incurredAtUtc)
    {
        db.TourOperatorDebts.Add(TourOperatorDebt.Create(operatorId, amount, incurredAtUtc, incurredAtUtc));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RunIfDueAsync_OperatorWithOutstandingDebt_OffsetsItAgainstTheSettlement()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(10));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-25));
        await SeedDebtAsync(db, 7, amount: 500_000m, incurredAtUtc: AtRunTime.AddDays(-1));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        var settlement = db.PayoutSettlements.Should().ContainSingle().Subject;
        settlement.DebtOffsetAmount.Should().Be(500_000m);
        settlement.PayoutAmount.Should().Be(900_000m, "Advance 1,400,000 minus the 500,000 debt offset");
    }

    [Fact]
    public async Task RunIfDueAsync_OperatorDebtUnrecoveredForFifteenDays_LocksRevokesTokensAndNotifies()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        var staff = new User { Id = 50, Email = "staff@test.local", FullName = "Staff", Role = UserRole.Staff, Status = AccountStatus.Active };
        db.Users.Add(staff);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = 7, TokenHash = "hash-1", ExpiresAtUtc = AtRunTime.AddDays(30), CreatedAtUtc = AtRunTime.AddDays(-20),
        });
        await db.SaveChangesAsync();
        await SeedDebtAsync(db, 7, amount: 500_000m, incurredAtUtc: AtRunTime.AddDays(-15).AddMinutes(-1));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        var reloadedOperator = await db.Users.FindAsync(7L);
        reloadedOperator!.Status.Should().Be(AccountStatus.Locked);
        reloadedOperator.LockReason.Should().Be(DailyPayoutAutoLock.UnrecoveredDebtLockReason);
        reloadedOperator.LockedAtUtc.Should().Be(AtRunTime);

        var token = db.RefreshTokens.Should().ContainSingle(t => t.UserId == 7).Subject;
        token.RevokedAtUtc.Should().Be(AtRunTime);

        db.AuditLogs.Should().ContainSingle(a => a.ActionType == AuditActionTypes.TourOperatorAutoLocked && a.AffectedEntityId == 7);
        db.Notifications.Should().Contain(n => n.UserId == 7).And.Contain(n => n.UserId == 50);
    }

    [Fact]
    public async Task RunIfDueAsync_OperatorDebtLessThanFifteenDaysOld_DoesNotLock()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await SeedDebtAsync(db, 7, amount: 500_000m, incurredAtUtc: AtRunTime.AddDays(-14));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        var reloadedOperator = await db.Users.FindAsync(7L);
        reloadedOperator!.Status.Should().Be(AccountStatus.Active);
    }

    [Fact]
    public async Task UnlockTourOperator_WithOutstandingDebt_IsRefused()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        operatorUser.Lock(DailyPayoutAutoLock.UnrecoveredDebtLockReason, AtRunTime);
        await SeedDebtAsync(db, 7, amount: 500_000m, incurredAtUtc: AtRunTime.AddDays(-20));
        await db.SaveChangesAsync();
        var staff = new FakeCurrentUserService { UserId = 50, Role = nameof(UserRole.Staff) };
        var handler = new UnlockTourOperatorCommandHandler(db, staff, new PayoutTestHelpers.FixedClock(AtRunTime));

        var result = await handler.Handle(new UnlockTourOperatorCommand(7), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.OperatorHasOutstandingDebt); // MSG199
        operatorUser.Status.Should().Be(AccountStatus.Locked);
    }

    [Fact]
    public async Task UnlockTourOperator_WithDebtFullyRecovered_Succeeds()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        operatorUser.Lock(DailyPayoutAutoLock.UnrecoveredDebtLockReason, AtRunTime);
        var debt = TourOperatorDebt.Create(7, 500_000m, AtRunTime.AddDays(-20), AtRunTime.AddDays(-20));
        debt.Recover(500_000m);
        db.TourOperatorDebts.Add(debt);
        await db.SaveChangesAsync();
        var staff = new FakeCurrentUserService { UserId = 50, Role = nameof(UserRole.Staff) };
        var handler = new UnlockTourOperatorCommandHandler(db, staff, new PayoutTestHelpers.FixedClock(AtRunTime));

        var result = await handler.Handle(new UnlockTourOperatorCommand(7), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        operatorUser.Status.Should().Be(AccountStatus.Active);
        operatorUser.LockReason.Should().BeNull();
        db.AuditLogs.Should().ContainSingle(a => a.ActionType == AuditActionTypes.TourOperatorUnlocked);
    }

    [Fact]
    public async Task UnlockTourOperator_NotLocked_ReturnsOperatorNotLocked()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7, AccountStatus.Active);
        var staff = new FakeCurrentUserService { UserId = 50, Role = nameof(UserRole.Staff) };
        var handler = new UnlockTourOperatorCommandHandler(db, staff, new PayoutTestHelpers.FixedClock(AtRunTime));

        var result = await handler.Handle(new UnlockTourOperatorCommand(7), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.OperatorNotLocked);
    }

    [Fact]
    public async Task UnlockTourOperator_ByNonStaff_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        operatorUser.Lock(DailyPayoutAutoLock.UnrecoveredDebtLockReason, AtRunTime);
        await db.SaveChangesAsync();
        var admin = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.Administrator) };
        var handler = new UnlockTourOperatorCommandHandler(db, admin, new PayoutTestHelpers.FixedClock(AtRunTime));

        var result = await handler.Handle(new UnlockTourOperatorCommand(7), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.Forbidden);
    }
}
