using FluentAssertions;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Application.Features.Payouts.DailySettlement.Admin;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Payouts.Admin;

// DAILY-PAYOUT-TRANSFER, Task 9 (spec v6, UC-73, PD-10): [Retry Transfer] and [Mark as Resolved].
// Carry over and manual settlement are removed in v6 (not in SRS rev10-5).
public sealed class DailyPayoutFailureHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);

    private static FakeCurrentUserService AdminUser() =>
        new() { UserId = 2, Role = nameof(UserRole.Administrator) };

    private static async Task<PayoutBatch> SeedFailedBatchAsync(TestDbContext db)
    {
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 9), tourOperatorUserId: 7, Now.AddHours(-3));
        var settlement = PayoutSettlement.Create(1, 1, 7, new DateOnly(2026, 11, 9), PayoutSettlementKind.Advance,
            1_400_000m, 10m, 0m, 1_400_000m, 0m, 1_400_000m, Now.AddHours(-3));
        batch.AddSettlement(settlement);
        db.PayoutBatches.Add(batch);
        await db.SaveChangesAsync();
        batch.MarkProcessing(Now.AddHours(-3));
        batch.MarkFailed(Now.AddHours(-2));
        await db.SaveChangesAsync();
        return batch;
    }

    private static RetryFailedPayoutBatchCommandHandler CreateRetryHandler(
        TestDbContext db, PayoutTestHelpers.FixedClock clock, PayoutTransferOutcome gatewayOutcome) =>
        new(db, AdminUser(), clock,
            new DailyPayoutRetrySweepProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(gatewayOutcome)), clock));

    [Fact]
    public async Task Retry_ByAdministrator_WithSuccessfulGateway_SettlesTheBatch()
    {
        // SRS 3.10.6: [Retry Transfer] submits once, synchronously — a success settles at once.
        await using var db = TestDbContext.Create();
        var batch = await SeedFailedBatchAsync(db);
        var handler = CreateRetryHandler(db, new PayoutTestHelpers.FixedClock(Now), PayoutTransferOutcome.Succeeded);

        var result = await handler.Handle(new RetryFailedPayoutBatchCommand(batch.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        db.AuditLogs.Should().ContainSingle(a => a.ActionType == AuditActionTypes.PayoutSettlementRetried);
    }

    [Fact]
    public async Task Retry_ByAdministrator_WithFailingGateway_ReturnsToSettlementFailed()
    {
        // SRS 3.10.6, abnormal 4.a1: a retry that fails again goes straight back to
        // SettlementFailed (MSG176) — not another automatic hourly retry.
        await using var db = TestDbContext.Create();
        var admin = new User { Id = 99, Email = "admin@test.local", FullName = "Admin", Role = UserRole.Administrator, Status = AccountStatus.Active };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var batch = await SeedFailedBatchAsync(db);
        var handler = CreateRetryHandler(db, new PayoutTestHelpers.FixedClock(Now), PayoutTransferOutcome.Rejected);

        var result = await handler.Handle(new RetryFailedPayoutBatchCommand(batch.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        db.Notifications.Should().ContainSingle(n => n.UserId == admin.Id); // MSG176
    }

    [Fact]
    public async Task Retry_ByNonAdministrator_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedFailedBatchAsync(db);
        var staff = new FakeCurrentUserService { UserId = 3, Role = nameof(UserRole.Staff) };
        var handler = new RetryFailedPayoutBatchCommandHandler(db, staff, new PayoutTestHelpers.FixedClock(Now),
            new DailyPayoutRetrySweepProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), new PayoutTestHelpers.FixedClock(Now)));

        var result = await handler.Handle(new RetryFailedPayoutBatchCommand(batch.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Retry_ForBatchNotFailed_ReturnsNotFailed()
    {
        await using var db = TestDbContext.Create();
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 9), 7, Now);
        db.PayoutBatches.Add(batch);
        await db.SaveChangesAsync();
        var handler = CreateRetryHandler(db, new PayoutTestHelpers.FixedClock(Now), PayoutTransferOutcome.Succeeded);

        var result = await handler.Handle(new RetryFailedPayoutBatchCommand(batch.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.NotFailed); // MSG177
    }

    [Fact]
    public async Task MarkResolved_WithoutReason_ReturnsValidationError()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedFailedBatchAsync(db);
        var handler = new MarkPayoutBatchResolvedCommandHandler(db, AdminUser(), new PayoutTestHelpers.FixedClock(Now));

        var result = await handler.Handle(new MarkPayoutBatchResolvedCommand(batch.Id, "  "), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.ResolutionReasonRequired); // MSG178
    }

    [Fact]
    public async Task MarkResolved_WithReason_ResolvesBatchWithoutMovingMoney()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedFailedBatchAsync(db);
        var handler = new MarkPayoutBatchResolvedCommandHandler(db, AdminUser(), new PayoutTestHelpers.FixedClock(Now));

        var result = await handler.Handle(new MarkPayoutBatchResolvedCommand(batch.Id, "Operator confirmed receipt out of band"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        batch.Status.Should().Be(PayoutSettlementStatus.Resolved);
        batch.TransferReference.Should().BeNull();
        batch.Settlements.Single().Status.Should().Be(PayoutSettlementStatus.Resolved);
        db.AuditLogs.Should().ContainSingle(a => a.ActionType == AuditActionTypes.PayoutSettlementResolved);
    }

    [Fact]
    public async Task MarkResolved_ForBatchNotFailed_ReturnsNotFailed()
    {
        await using var db = TestDbContext.Create();
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 9), 7, Now);
        db.PayoutBatches.Add(batch);
        await db.SaveChangesAsync();
        var handler = new MarkPayoutBatchResolvedCommandHandler(db, AdminUser(), new PayoutTestHelpers.FixedClock(Now));

        var result = await handler.Handle(new MarkPayoutBatchResolvedCommand(batch.Id, "reason"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.NotFailed);
    }

    [Fact]
    public async Task MarkResolved_ByNonAdministrator_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedFailedBatchAsync(db);
        var staff = new FakeCurrentUserService { UserId = 3, Role = nameof(UserRole.Staff) };
        var handler = new MarkPayoutBatchResolvedCommandHandler(db, staff, new PayoutTestHelpers.FixedClock(Now));

        var result = await handler.Handle(new MarkPayoutBatchResolvedCommand(batch.Id, "reason"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.Forbidden);
    }
}