using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Api.Controllers.V1.Requests;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Application.Features.Tickets.Get;
using TripMate.Application.Features.Tickets.Refresh;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Security;

namespace TripMate.Api.IntegrationTests.Tickets;

/// <summary>
/// Proves on SQL Server what the in-memory provider cannot: the snapshot query translates, the filtered
/// unique index decides concurrent first views and refreshes, and the whole UC-28 → UC-29 → UC-41 flow.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class TravelerTicketsSqlServerTests
{
    private const int Concurrency = 8;
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task ConcurrentFirstViews_AllReturnTheSameQrAndLeaveOneActiveRow()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var ticketId = await seed.AddTicketAsync(await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        var results = await RunConcurrentlyAsync(database, context =>
            new GetTravelerTicketQueryHandler(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier(),
                new FixedClock(Now)).Handle(GetTravelerTicketQuery.ForTicket(seed.TravelerId, ticketId),
                CancellationToken.None));

        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.Qr!.Content).Distinct().Should().ContainSingle();
        (await ActiveCountAsync(database, ticketId)).Should().Be(1);
    }

    [SqlServerFact]
    public async Task ConcurrentRefreshes_AllSucceedAndLeaveOneActiveRow()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var ticketId = await seed.AddTicketAsync(await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        await using (var context = database.CreateDbContext())
        {
            (await new GetTravelerTicketQueryHandler(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier(),
                    new FixedClock(Now))
                .Handle(GetTravelerTicketQuery.ForTicket(seed.TravelerId, ticketId), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        }

        var results = await RunConcurrentlyAsync(database, context =>
            new RefreshTicketQrCommandHandler(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier(),
                new FixedClock(Now)).Handle(new RefreshTicketQrCommand(seed.TravelerId, ticketId),
                CancellationToken.None));

        results.Should().OnlyContain(result => result.IsSuccess);
        (await ActiveCountAsync(database, ticketId)).Should().Be(1);
        var activeToken = await database.ExecuteScalarAsync<string>(
            $"SELECT qr_token FROM commerce.TicketQrCodes WHERE ticket_id = {ticketId} AND status = 'Active';");
        results.Select(result => result.Value.Qr!.Content).Should().Contain(content => content.Contains(activeToken));
    }

    [SqlServerFact]
    public async Task PaymentThenViewThenRefreshThenCancel_FollowsTheTicketLifecycle()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.PendingPayment, PaymentStatus.Unpaid);
        await database.ExecuteNonQueryAsync($"""
            UPDATE commerce.Bookings
            SET booked_at = '{Now.AddMinutes(-5).UtcDateTime:s}', payment_expires_at = '{Now.AddMinutes(10).UtcDateTime:s}'
            WHERE booking_id = {bookingId};
            """);
        await PayAsync(database, bookingId);

        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString,
            dateTimeProviderFactory: _ => new FixedClock(Now));
        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        using var viewed = await client.GetAsync($"/api/v1/traveler/bookings/{bookingId}/ticket");
        using var viewedBody = await ReadAsync(viewed, HttpStatusCode.OK);
        var ticketId = viewedBody.RootElement.GetProperty("ticketId").GetInt64();
        viewedBody.RootElement.GetProperty("ticketStatus").GetString().Should().Be(nameof(TicketDisplayStatus.NotYetActive));
        var firstContent = viewedBody.RootElement.GetProperty("qr").GetProperty("content").GetString()!;
        Codec().TryVerify(firstContent, out var verification).Should().BeTrue();
        verification!.TicketId.Should().Be(ticketId);

        using var refreshed = await client.PostAsync($"/api/v1/traveler/tickets/{ticketId}/qr-code/refresh", null);
        using var refreshedBody = await ReadAsync(refreshed, HttpStatusCode.OK);
        refreshedBody.RootElement.GetProperty("qr").GetProperty("content").GetString().Should().NotBe(firstContent);
        (await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM commerce.TicketQrCodes WHERE ticket_id = {ticketId} AND status = 'Revoked';"))
            .Should().Be(1);

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var cancelled = await client.PostAsJsonAsync($"/api/v1/bookings/{bookingId}/cancellation",
            new CancelBookingRequest("ChangeOfPlans", "UC-29 lifecycle test"));
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());

        using var afterCancel = await client.GetAsync($"/api/v1/traveler/tickets/{ticketId}");
        using var afterCancelBody = await ReadAsync(afterCancel, HttpStatusCode.OK);
        afterCancelBody.RootElement.GetProperty("ticketStatus").GetString().Should().Be(nameof(TicketDisplayStatus.Cancelled));
        afterCancelBody.RootElement.GetProperty("qr").ValueKind.Should().Be(JsonValueKind.Null);
        (await ActiveCountAsync(database, ticketId)).Should().Be(0, "cancellation revokes the QR (BR-86)");
    }

    private static async Task PayAsync(SqlServerTestDatabase database, long bookingId)
    {
        await using var context = database.CreateDbContext();
        var booking = await context.Bookings.SingleAsync(candidate => candidate.Id == bookingId);
        var transaction = PaymentTransaction.Create(bookingId, PaymentGateway.Mock, booking.TotalAmount, 290_001, Now);
        context.PaymentTransactions.Add(transaction);
        await context.SaveChangesAsync();

        var completed = await new PaymentCompletionProcessor(context, new FixedClock(Now)).CompleteAsync(
            transaction.Id, orderCode: null, PaymentTransactionStatus.Success, "MOCK-REF", "raw",
            booking.TotalAmount, "VND", CancellationToken.None);
        completed.IsSuccess.Should().BeTrue();
    }

    private static async Task<List<Result<TicketResponse>>> RunConcurrentlyAsync(SqlServerTestDatabase database,
        Func<ApplicationDbContext, Task<Result<TicketResponse>>> operation)
    {
        using var start = new SemaphoreSlim(0, Concurrency);
        var tasks = Enumerable.Range(0, Concurrency).Select(async _ =>
        {
            await using var context = database.CreateDbContext();
            await start.WaitAsync();
            return await operation(context);
        }).ToList();
        start.Release(Concurrency);
        return [.. await Task.WhenAll(tasks)];
    }

    private static Task<int> ActiveCountAsync(SqlServerTestDatabase database, long ticketId) =>
        database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM commerce.TicketQrCodes WHERE ticket_id = {ticketId} AND status = 'Active';");

    private static AesGcmHmacTicketQrCodec Codec() => new(Options.Create(new TicketQrOptions
    {
        EncryptionKey = TripMateApiFactory.TicketQrEncryptionKey,
        SigningKey = TripMateApiFactory.TicketQrSigningKey,
    }));

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, "the response body was {0}", payload);
        return JsonDocument.Parse(payload);
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}
