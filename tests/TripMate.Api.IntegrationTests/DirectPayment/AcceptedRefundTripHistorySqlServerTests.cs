using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TripHistory.Common;

namespace TripMate.Api.IntegrationTests.DirectPayment;

public sealed class AcceptedRefundTripHistorySqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RefundClassificationTranslatesToSqlAndKeepsOwnershipAndCounts()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commerce;");
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE commerce.Bookings(booking_id bigint, traveler_user_id bigint, tour_schedule_id bigint, status varchar(32));
            CREATE TABLE payment.DirectRefundCases(booking_id bigint, refund_amount_vnd bigint, status varchar(40));
            CREATE TABLE payment.Refunds(booking_id bigint, amount decimal(12,2), status varchar(30));
            INSERT commerce.Bookings VALUES (1,7,1,'Completed'),(2,7,1,'Completed'),(3,8,1,'Completed');
            INSERT payment.DirectRefundCases VALUES (1,3000,'AwaitingRecipient'),(3,3000,'Completed');
            INSERT payment.Refunds VALUES (2,1000,'Rejected');
            """);
        await using var db = database.CreateDbContext();
        var ids = TripRefundHistory.BookingIds(db);
        var cancelled = await TripBookingStream.Filter(db.Bookings, new(7, "Cancelled", new(null, null)), ids).Select(b => b.Id).ToListAsync();
        Assert.Equal(new long[] { 1 }, cancelled);
        var completed = await TripBookingStream.Filter(db.Bookings, new(7, "Completed", new(null, null)), ids).Select(b => b.Id).ToListAsync();
        Assert.Equal(new long[] { 2 }, completed);
        var states = await TripRefundHistory.LoadAsync(db, [1, 2], default);
        Assert.Equal("RefundPending", states[1].DisplayStatus(10000));
        Assert.False(states.ContainsKey(2));
    }
}