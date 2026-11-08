using FluentAssertions;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Application.Features.Payouts.DailySettlement.Admin;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Payouts.Admin;

// DAILY-PAYOUT-TRANSFER, Task 9 (UC-73, PD-10).
public sealed class DailyPayoutFailureHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);

    private static TripMate.Application.UnitTests.TestUtilities.FakeCurrentUserService AdminUser() =>
        new() { UserId = 2, Role = nameof(UserRole.Administrator) };

    private static async Task<PayoutBatch> SeedFailedBatchAsync(TestDbContext db)
    {
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 9), tourOperatorUserId: 7, Now.AddHours(-3));
        var settlement = PayoutSettlement.Create(1, 1, 7, new DateOnly(2026, 11, 9), PayoutSettlementKind.Main,
            2_000_000m, 10m, 200_000m, 1_800_000m, 0m, 1_800_000m, Now.AddHours(-3));
        batch.AddSettlement(settlement);
        db.PayoutBatches.Add(batch);
        await db.SaveChangesAsync();
        batch.MarkProcessing(Now.AddHours(-3));
        batch.MarkFailed(Now.AddHours(-2));
        await db.SaveChangesAsync();
        return batch;
    }

    [Fact]
    public async Task Retry_ByAdministrator_MovesBatchBackToProcessing()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedFailedBatchAsync(db);
        var handler = new RetryFailedPayoutBatchCommandHandler(db, AdminUser(), new PayoutTestHelpers.FixedClock(Now));

        var result = await handler.Handle(new RetryFailedPayoutBatchCommand(batch.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        batch.Status.Should().Be(PayoutSettlementStatus.Processing);
        db.AuditLogs.Should().ContainSingle();
    }

    [Fact]
    public async Task Retry_ByNonAdministrator_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedFailedBatchAsync(db);
        var staff = new FakeCurrentUserService { UserId = 3, Role = nameof(UserRole.Staff) };
        var handler = new RetryFailedPayoutBatchCommandHandler(db, staff, new PayoutTestHelpers.FixedClock(Now));

        var result = await handler.Handle(new RetryFailedPayoutBatchCommand(batch.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.Forbidden);
    }

    [Fact]
    public async Task CarryOver_ByAdministrator_CreatesAdjustmentAndMarksCarriedOver()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedFailedBatchAsync(db);
        var handler = new CarryOverFailedPayoutBatchCommandHandler(db, AdminUser(), new PayoutTestHelpers.FixedClock(Now));

        var result = await handler.Handle(new CarryOverFailedPayoutBatchCommand(batch.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        batch.Status.Should().Be(PayoutSettlementStatus.CarriedOver);
        batch.TotalAmount.Should().Be(1_800_000m, "PC-02: the batch keeps its own amount");

        var adjustment = db.PayoutSettlementAdjustments.Should().ContainSingle().Subject;
        adjustment.Amount.Should().Be(1_800_000m);
        adjustment.TourOperatorUserId.Should().Be(7);
        adjustment.IsOutstanding.Should().BeTrue();
    }

    [Fact]
    public async Task RecordManual_WithoutReference_ReturnsValidationError()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedFailedBatchAsync(db);
        var handler = new RecordManualPayoutSettlementCommandHandler(db, AdminUser(), new PayoutTestHelpers.FixedClock(Now));

        var result = await handler.Handle(new RecordManualPayoutSettlementCommand(batch.Id, "  "), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.ManualReferenceRequired);
    }

    [Fact]
    public async Task RecordManual_WithReference_SettlesBatch()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedFailedBatchAsync(db);
        var handler = new RecordManualPayoutSettlementCommandHandler(db, AdminUser(), new PayoutTestHelpers.FixedClock(Now));

        var result = await handler.Handle(new RecordManualPayoutSettlementCommand(batch.Id, "BANK-SLIP-001"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.TransferReference.Should().Be("BANK-SLIP-001");
        batch.Settlements.Single().TransferReference.Should().Be("BANK-SLIP-001");
    }

    [Fact]
    public async Task Retry_ForBatchNotFailed_ReturnsNotFailed()
    {
        await using var db = TestDbContext.Create();
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 9), 7, Now);
        db.PayoutBatches.Add(batch);
        await db.SaveChangesAsync();
        var handler = new RetryFailedPayoutBatchCommandHandler(db, AdminUser(), new PayoutTestHelpers.FixedClock(Now));

        var result = await handler.Handle(new RetryFailedPayoutBatchCommand(batch.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.NotFailed);
    }
}
