using FluentAssertions;

using Microsoft.EntityFrameworkCore;
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

        var payout = context.Model.FindEntityType(typeof(Payout))!;
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
        Column(payout, nameof(Payout.ConfirmedBy)).Should().Be("confirmed_by");
        Column(payout, nameof(Payout.ConfirmedAtUtc)).Should().Be("confirmed_at");

        payout.FindProperty(nameof(Payout.RequestedAtUtc))!.GetValueConverter().Should().NotBeNull();
        payout.FindProperty(nameof(Payout.RequestedAtUtc))!.IsNullable.Should().BeTrue();
        payout.FindProperty(nameof(Payout.ConfirmedAtUtc))!.IsNullable.Should().BeTrue();
        payout.FindProperty(nameof(Payout.Status))!.IsNullable.Should().BeFalse();
        payout.GetProperties().Single(property => property.Name == nameof(Payout.GrossRevenue))
            .GetPrecision().Should().Be(14);
        payout.GetForeignKeys().Should().Contain(key => key.PrincipalEntityType.ClrType == typeof(OperatorProfile));
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