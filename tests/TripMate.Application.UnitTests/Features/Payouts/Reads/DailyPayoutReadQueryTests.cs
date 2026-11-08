using FluentAssertions;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Application.Features.Payouts.DailySettlement.Reads;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Payouts.Reads;

// DAILY-PAYOUT-TRANSFER, Task 10 (UC-72, UC-73, PD-13).
public sealed class DailyPayoutReadQueryTests
{
    private static async Task<PayoutBatch> SeedSettledBatchAsync(TestDbContext db, long operatorId, long bookingId)
    {
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 9), operatorId, DateTimeOffset.UtcNow);
        var settlement = PayoutSettlement.Create(1, bookingId, operatorId, new DateOnly(2026, 11, 9), PayoutSettlementKind.Main,
            2_000_000m, 10m, 200_000m, 1_800_000m, 0m, 1_800_000m, DateTimeOffset.UtcNow);
        batch.AddSettlement(settlement);
        db.PayoutBatches.Add(batch);
        await db.SaveChangesAsync();
        batch.MarkProcessing(DateTimeOffset.UtcNow);
        batch.MarkSettled("REF-1", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return batch;
    }

    [Fact]
    public async Task GetPayoutSettlements_AsStaff_ReturnsAllOperatorsSettlements()
    {
        await using var db = TestDbContext.Create();
        await SeedSettledBatchAsync(db, operatorId: 7, bookingId: 1);
        await SeedSettledBatchAsync(db, operatorId: 8, bookingId: 2);
        var staff = new FakeCurrentUserService { UserId = 3, Role = nameof(UserRole.Staff) };
        var handler = new GetPayoutSettlementsQueryHandler(db, staff);

        var result = await handler.Handle(new GetPayoutSettlementsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task GetPayoutSettlements_AsTourOperator_IsForbidden()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.TourOperator) };
        var handler = new GetPayoutSettlementsQueryHandler(db, operatorUser);

        var result = await handler.Handle(new GetPayoutSettlementsQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DailyPayoutErrorCodes.Forbidden);
    }

    [Fact]
    public async Task GetOperatorPayoutSettlementHistory_ReturnsOnlyThatOperatorsRows()
    {
        await using var db = TestDbContext.Create();
        await SeedSettledBatchAsync(db, operatorId: 7, bookingId: 1);
        await SeedSettledBatchAsync(db, operatorId: 8, bookingId: 2);
        var operatorUser = new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.TourOperator) };
        var handler = new GetOperatorPayoutSettlementHistoryQueryHandler(db, operatorUser);

        var result = await handler.Handle(new GetOperatorPayoutSettlementHistoryQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(i => i.TourOperatorUserId == 7);
    }

    [Fact]
    public async Task GetFailedPayoutBatchDetail_AsAdministrator_ReturnsAttemptHistory()
    {
        await using var db = TestDbContext.Create();
        var batch = PayoutBatch.Create(new DateOnly(2026, 11, 9), 7, DateTimeOffset.UtcNow);
        var settlement = PayoutSettlement.Create(1, 1, 7, new DateOnly(2026, 11, 9), PayoutSettlementKind.Main,
            2_000_000m, 10m, 200_000m, 1_800_000m, 0m, 1_800_000m, DateTimeOffset.UtcNow);
        batch.AddSettlement(settlement);
        db.PayoutBatches.Add(batch);
        await db.SaveChangesAsync();
        batch.MarkProcessing(DateTimeOffset.UtcNow);
        batch.MarkFailed(DateTimeOffset.UtcNow);
        db.PayoutSettlementAttempts.Add(PayoutSettlementAttempt.Create(
            batch.Id, 1, DateTimeOffset.UtcNow, "SIM-ref", PayoutSettlementAttempt.OutcomeFailed, "bank_down"));
        await db.SaveChangesAsync();

        var admin = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.Administrator) };
        var handler = new GetFailedPayoutBatchDetailQueryHandler(db, admin);

        var result = await handler.Handle(new GetFailedPayoutBatchDetailQuery(batch.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        result.Value.Attempts.Should().ContainSingle(a => a.FailureReason == "bank_down");
    }
}
