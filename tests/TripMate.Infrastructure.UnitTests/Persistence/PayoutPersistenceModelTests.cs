using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class PayoutPersistenceModelTests
{
    [Fact]
    public void Model_MapsPaymentPayoutSchema()
    {
        using var context = CreateContext();

        var payout = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Payout))!;
        payout.GetTableName().Should().Be("Payouts");
        payout.GetSchema().Should().Be("payment");
        Column(payout, nameof(Payout.Id)).Should().Be("payout_id");
        Column(payout, nameof(Payout.OperatorUserId)).Should().Be("operator_user_id");
        Column(payout, nameof(Payout.PeriodStart)).Should().Be("period_start");
        Column(payout, nameof(Payout.PeriodEnd)).Should().Be("period_end");
        Column(payout, nameof(Payout.GrossRevenue)).Should().Be("gross_revenue");
        Column(payout, nameof(Payout.CommissionAmount)).Should().Be("commission_amount");
        Column(payout, nameof(Payout.NetAmount)).Should().Be("net_amount");
        Column(payout, nameof(Payout.Status)).Should().Be("status");
        Column(payout, nameof(Payout.RequestedAtUtc)).Should().Be("requested_at");
        Column(payout, nameof(Payout.ReviewedBy)).Should().Be("reviewed_by");
        Column(payout, nameof(Payout.ReviewedAtUtc)).Should().Be("reviewed_at");
        Column(payout, nameof(Payout.RejectionReason)).Should().Be("rejection_reason");
        Column(payout, nameof(Payout.SettlementAuthorizedBy)).Should().Be("settlement_authorized_by");
        Column(payout, nameof(Payout.SettlementAuthorizedAtUtc)).Should().Be("settlement_authorized_at");
        Column(payout, nameof(Payout.GatewayReference)).Should().Be("gateway_reference");
        Column(payout, nameof(Payout.GatewayFailureReason)).Should().Be("gateway_failure_reason");
        Column(payout, nameof(Payout.GatewayAttemptCount)).Should().Be("gateway_attempt_count");
        Column(payout, nameof(Payout.LastGatewayAttemptAtUtc)).Should().Be("last_gateway_attempt_at");

        payout.FindProperty(nameof(Payout.RequestedAtUtc))!.GetValueConverter().Should().NotBeNull();
        payout.FindProperty(nameof(Payout.RequestedAtUtc))!.IsNullable.Should().BeTrue();
        payout.FindProperty(nameof(Payout.ReviewedAtUtc))!.IsNullable.Should().BeTrue();
        payout.FindProperty(nameof(Payout.SettlementAuthorizedAtUtc))!.IsNullable.Should().BeTrue();
        payout.FindProperty(nameof(Payout.LastGatewayAttemptAtUtc))!.IsNullable.Should().BeTrue();
        payout.FindProperty(nameof(Payout.Status))!.IsNullable.Should().BeFalse();
        payout.FindProperty(nameof(Payout.GatewayAttemptCount))!.GetDefaultValue().Should().Be(0);
        payout.GetProperties().Single(property => property.Name == nameof(Payout.GrossRevenue))
            .GetPrecision().Should().Be(14);
        payout.GetForeignKeys().Should().Contain(key => key.PrincipalEntityType.ClrType == typeof(OperatorProfile));
        payout.GetCheckConstraints().Select(constraint => constraint.Name).Should().BeEquivalentTo(
            "CK_Payouts_PeriodOrder",
            "CK_Payouts_NetAmount",
            "CK_Payouts_Status",
            "CK_Payouts_LifecycleState",
            "CK_Payouts_PayableNet");
        payout.GetCheckConstraints().Single(constraint => constraint.Name == "CK_Payouts_NetAmount").Sql.Should().Be(
            "payable_net_before_adjustments IS NULL OR net_amount = payable_net_before_adjustments - adjustments_applied");
    }

    [Fact]
    public void LifecycleStatuses_MatchPersistedPayoutContract()
    {
        Payout.AllStatuses.Should().BeEquivalentTo(
            Payout.StatusPendingReview,
            Payout.StatusApprovedForSettlement,
            Payout.StatusSettlementProcessing,
            Payout.StatusSettled,
            Payout.StatusSettlementFailed,
            Payout.StatusRejected,
            Payout.StatusCancelled);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static string? Column(IEntityType entityType, string propertyName)
    {
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        return entityType.FindProperty(propertyName)!.GetColumnName(table);
    }
}