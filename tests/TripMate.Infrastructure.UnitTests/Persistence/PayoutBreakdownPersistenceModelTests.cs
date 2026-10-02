using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class PayoutBreakdownPersistenceModelTests
{
    [Fact]
    public void Model_MapsPayoutBreakdownSchema()
    {
        using var context = CreateContext();

        var item = context.Model.FindEntityType(typeof(PayoutItem))!;
        item.GetTableName().Should().Be("PayoutItems");
        item.GetSchema().Should().Be("payment");
        Column(item, nameof(PayoutItem.Id)).Should().Be("payout_item_id");
        Column(item, nameof(PayoutItem.PayoutId)).Should().Be("payout_id");
        Column(item, nameof(PayoutItem.BookingId)).Should().Be("booking_id");
        Column(item, nameof(PayoutItem.Amount)).Should().Be("amount");
        item.GetForeignKeys().Should().Contain(key => key.PrincipalEntityType.ClrType == typeof(Payout));
        item.GetForeignKeys().Should().Contain(key => key.PrincipalEntityType.ClrType == typeof(Booking));

        var booking = context.Model.FindEntityType(typeof(Booking))!;
        booking.GetTableName().Should().Be("Bookings");
        booking.GetSchema().Should().Be("commerce");
        Column(booking, nameof(Booking.Id)).Should().Be("booking_id");
        Column(booking, nameof(Booking.BookingCode)).Should().Be("booking_code");
        Column(booking, nameof(Booking.TourScheduleId)).Should().Be("tour_schedule_id");
        booking.FindProperty(nameof(Booking.TourScheduleId))!.IsNullable.Should().BeTrue();
        booking.FindProperty(nameof(Booking.BookedAtUtc))!.GetValueConverter().Should().NotBeNull();

        var transaction = context.Model.FindEntityType(typeof(PaymentTransaction))!;
        transaction.GetTableName().Should().Be("PaymentTransactions");
        transaction.GetSchema().Should().Be("payment");
        Column(transaction, nameof(PaymentTransaction.Id)).Should().Be("transaction_id");
        Column(transaction, nameof(PaymentTransaction.TransactionType)).Should().Be("transaction_type");
        Column(transaction, nameof(PaymentTransaction.Amount)).Should().Be("amount");
        transaction.GetProperties().Single(property => property.Name == nameof(PaymentTransaction.Amount))
            .GetPrecision().Should().Be(12);

        var refund = context.Model.FindEntityType(typeof(Refund))!;
        refund.GetTableName().Should().Be("Refunds");
        refund.GetSchema().Should().Be("payment");
        Column(refund, nameof(Refund.Id)).Should().Be("refund_id");
        Column(refund, nameof(Refund.Status)).Should().Be("status");
        refund.FindProperty(nameof(Refund.ProcessedAtUtc))!.IsNullable.Should().BeTrue();
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