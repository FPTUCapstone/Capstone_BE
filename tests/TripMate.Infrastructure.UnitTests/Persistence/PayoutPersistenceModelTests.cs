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
            "CK_Payouts_BankBin",
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

    [Fact]
    public void Model_MapsPayoutBankBinAsOptionalSixCharacterColumn()
    {
        using var context = CreateContext();

        var payout = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Payout))!;
        Column(payout, nameof(Payout.BankBin)).Should().Be("bank_bin");
        var property = payout.FindProperty(nameof(Payout.BankBin))!;
        property.IsNullable.Should().BeTrue();
        property.GetMaxLength().Should().Be(6);
        property.IsFixedLength().Should().BeTrue();
        property.IsUnicode().Should().BeFalse();
    }

    [Fact]
    public void Model_MapsBankCatalogueKeyedByBin()
    {
        using var context = CreateContext();

        var bank = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Bank))!;
        bank.GetTableName().Should().Be("Banks");
        bank.GetSchema().Should().Be("payment");
        bank.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(Bank.BankBin));
        Column(bank, nameof(Bank.BankBin)).Should().Be("bank_bin");
        Column(bank, nameof(Bank.ShortName)).Should().Be("short_name");
        Column(bank, nameof(Bank.OfficialName)).Should().Be("official_name");
        bank.FindProperty(nameof(Bank.BankBin))!.GetMaxLength().Should().Be(6);
        bank.GetIndexes().Count(index => index.IsUnique).Should().Be(2);
        bank.GetCheckConstraints().Select(constraint => constraint.Name)
            .Should().BeEquivalentTo("CK_Banks_Bin");
    }

    [Fact]
    public void Model_MapsPayoutSettlementWorkItemWithOneItemPerPayout()
    {
        using var context = CreateContext();

        var item = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(PayoutSettlementWorkItem))!;
        item.GetTableName().Should().Be("PayoutSettlementWorkItems");
        item.GetSchema().Should().Be("payment");
        Column(item, nameof(PayoutSettlementWorkItem.Id)).Should().Be("work_item_id");
        Column(item, nameof(PayoutSettlementWorkItem.PayoutId)).Should().Be("payout_id");
        Column(item, nameof(PayoutSettlementWorkItem.State)).Should().Be("state");
        Column(item, nameof(PayoutSettlementWorkItem.AttemptCount)).Should().Be("attempt_count");
        Column(item, nameof(PayoutSettlementWorkItem.NextAttemptAtUtc)).Should().Be("next_attempt_at");
        Column(item, nameof(PayoutSettlementWorkItem.LeaseToken)).Should().Be("lease_token");
        Column(item, nameof(PayoutSettlementWorkItem.LeaseExpiresAtUtc)).Should().Be("lease_expires_at");
        Column(item, nameof(PayoutSettlementWorkItem.GatewayReference)).Should().Be("gateway_reference");
        Column(item, nameof(PayoutSettlementWorkItem.SubmittedAtUtc)).Should().Be("submitted_at");
        Column(item, nameof(PayoutSettlementWorkItem.PollDeadlineAtUtc)).Should().Be("poll_deadline_at");
        Column(item, nameof(PayoutSettlementWorkItem.PollCount)).Should().Be("poll_count");
        Column(item, nameof(PayoutSettlementWorkItem.LastPolledAtUtc)).Should().Be("last_polled_at");
        Column(item, nameof(PayoutSettlementWorkItem.LastErrorCode)).Should().Be("last_error_code");
        Column(item, nameof(PayoutSettlementWorkItem.CreatedAtUtc)).Should().Be("created_at");
        Column(item, nameof(PayoutSettlementWorkItem.CompletedAtUtc)).Should().Be("completed_at");
        Column(item, nameof(PayoutSettlementWorkItem.RowVersion)).Should().Be("row_version");

        item.FindProperty(nameof(PayoutSettlementWorkItem.RowVersion))!.IsConcurrencyToken.Should().BeTrue();
        item.FindProperty(nameof(PayoutSettlementWorkItem.NextAttemptAtUtc))!.IsNullable.Should().BeTrue();
        item.FindProperty(nameof(PayoutSettlementWorkItem.GatewayReference))!.IsNullable.Should().BeTrue();
        item.GetForeignKeys().Should().ContainSingle(key => key.PrincipalEntityType.ClrType == typeof(Payout));
        item.GetIndexes().Should().Contain(index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(PayoutSettlementWorkItem.PayoutId) }));
        item.GetIndexes().Should().Contain(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(PayoutSettlementWorkItem.State), nameof(PayoutSettlementWorkItem.NextAttemptAtUtc) }));
        item.GetCheckConstraints().Select(constraint => constraint.Name)
            .Should().BeEquivalentTo("CK_PayoutSettlementWorkItems_State", "CK_PayoutSettlementWorkItems_Counts");
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