using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

using static TripMate.Api.IntegrationTests.Tickets.TicketApiSeed;

namespace TripMate.Api.IntegrationTests.Tickets;

[Collection(nameof(TripMateApiFactory))]
public sealed class TravelerTicketsEndpointTests
{
    private sealed record Seeded(long ValidTicketId, long ValidBookingId, long UsedTicketId, long PendingBookingId,
        long PaidWithoutTicketBookingId);

    private static async Task<(TripMateApiFactory Factory, Seeded Seed)> NewFactoryAsync()
    {
        var factory = new TripMateApiFactory(dateTimeProviderFactory: _ => new FixedClock(InWindow));
        var seed = await factory.WithDbContextAsync(async db =>
        {
            var data = new TicketApiSeed(db);
            await data.AddPeopleAsync();
            var validBooking = await data.AddBookingAsync(Alice, BookingStatus.Confirmed, PaymentStatus.Paid);
            var validTicket = await data.AddTicketAsync(validBooking);
            var usedTicket = await data.AddTicketAsync(
                await data.AddBookingAsync(Alice, BookingStatus.Completed, PaymentStatus.Paid), TicketStatus.Used);
            var pending = await data.AddBookingAsync(Alice, BookingStatus.PendingPayment, PaymentStatus.Unpaid);
            var paidWithoutTicket = await data.AddBookingAsync(Alice, BookingStatus.Confirmed, PaymentStatus.Paid);
            return new Seeded(validTicket, validBooking, usedTicket, pending, paidWithoutTicket);
        });
        return (factory, seed);
    }

    // --- authentication, authorization and ids ---

    [Theory]
    [InlineData("GET", "/api/v1/traveler/tickets/1")]
    [InlineData("GET", "/api/v1/traveler/bookings/1/ticket")]
    [InlineData("POST", "/api/v1/traveler/tickets/1/qr-code/refresh")]
    public async Task Anonymous_IsUnauthorizedWithTheFeatureCode(string method, string route)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route));

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "ticket.unauthorized");
    }

    [Theory]
    [InlineData(UserRole.TourOperator)]
    [InlineData(UserRole.Administrator)]
    public async Task NonTraveler_IsForbiddenWithTheFeatureCode(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(Operator, role);

        using var response = await client.GetAsync("/api/v1/traveler/tickets/1");

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "ticket.forbidden");
    }

    [Fact]
    public async Task InactiveTraveler_IsForbidden()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateAuthenticatedClient(LockedTraveler, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/traveler/tickets/{seed.ValidTicketId}");

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "ticket.forbidden");
    }

    [Theory]
    [InlineData("GET", "/api/v1/traveler/tickets/0")]
    [InlineData("GET", "/api/v1/traveler/tickets/-1")]
    [InlineData("GET", "/api/v1/traveler/tickets/abc")]
    [InlineData("GET", "/api/v1/traveler/tickets/99999999999999999999")]
    [InlineData("GET", "/api/v1/traveler/bookings/0/ticket")]
    [InlineData("GET", "/api/v1/traveler/bookings/abc/ticket")]
    [InlineData("POST", "/api/v1/traveler/tickets/0/qr-code/refresh")]
    [InlineData("POST", "/api/v1/traveler/tickets/abc/qr-code/refresh")]
    public async Task NonPositiveOrNonNumericId_IsBadRequest(string method, string route)
    {
        var (factory, _) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- view ---

    [Fact]
    public async Task GetTicket_ReturnsTheContractBodyWithAVerifiableQr()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/traveler/tickets/{seed.ValidTicketId}");

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        var root = body.RootElement;
        root.GetProperty("ticketId").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("ticketId").GetInt64().Should().Be(seed.ValidTicketId);
        root.GetProperty("bookingId").GetInt64().Should().Be(seed.ValidBookingId);
        root.GetProperty("ticketCode").GetString().Should().Be($"TK-{seed.ValidBookingId:D10}");
        root.GetProperty("bookingCode").GetString().Should().Be($"BK-20261017-{seed.ValidBookingId:D5}");
        root.GetProperty("ticketStatus").GetString().Should().Be("Valid");
        root.GetProperty("tour").GetProperty("tourId").GetInt64().Should().Be(45);
        root.GetProperty("tour").GetProperty("title").GetString().Should().Be("Hoi An Lantern Tour");
        root.GetProperty("operatorName").GetString().Should().Be("Hoi An Travel Co.");
        root.GetProperty("schedule").GetProperty("meetingPoint").GetString().Should().Be("Old Town gate");
        root.GetProperty("participantCount").GetInt32().Should().Be(1);
        root.GetProperty("participants").GetProperty("adult").GetInt32().Should().Be(1);
        root.GetProperty("validity").GetProperty("departureDate").GetString().Should().Be("2026-10-30");
        root.GetProperty("validity").GetProperty("timeZone").GetString().Should().Be("Asia/Ho_Chi_Minh");
        root.GetProperty("validity").GetProperty("validFromUtc").GetDateTimeOffset()
            .Should().Be(new DateTimeOffset(2026, 10, 29, 17, 0, 0, TimeSpan.Zero));
        root.GetProperty("checkedInAtUtc").ValueKind.Should().Be(JsonValueKind.Null);

        var content = root.GetProperty("qr").GetProperty("content").GetString()!;
        content.Should().StartWith("TM1.");
        factory.Services.GetRequiredService<ITicketQrCodec>().TryVerify(content, out var verification)
            .Should().BeTrue();
        verification!.TicketId.Should().Be(seed.ValidTicketId);
    }

    [Fact]
    public async Task GetTicket_WhenUsed_WritesQrAsNull()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/traveler/tickets/{seed.UsedTicketId}");

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        body.RootElement.GetProperty("ticketStatus").GetString().Should().Be("Used");
        body.RootElement.TryGetProperty("qr", out var qr).Should().BeTrue("qr is written as null, not omitted");
        qr.ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("checkedInAtUtc").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task GetBookingTicket_ReturnsTheSameTicket()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/traveler/bookings/{seed.ValidBookingId}/ticket");

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        body.RootElement.GetProperty("ticketId").GetInt64().Should().Be(seed.ValidTicketId);
    }

    [Fact]
    public async Task GetTicket_OfAnotherTraveler_IsForbidden()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateAuthenticatedClient(Bob, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/traveler/tickets/{seed.ValidTicketId}");

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "ticket.forbidden");
    }

    [Theory]
    [InlineData("/api/v1/traveler/tickets/404")]
    [InlineData("/api/v1/traveler/bookings/404/ticket")]
    public async Task Unknown_IsNotFound(string route)
    {
        var (factory, _) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync(route);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "ticket.not_found");
    }

    [Fact]
    public async Task GetBookingTicket_ForAnUnpaidBooking_IsConflict()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/traveler/bookings/{seed.PendingBookingId}/ticket");

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "ticket.booking_not_confirmed");
    }

    [Fact]
    public async Task GetBookingTicket_ForAPaidBookingWithoutTicket_IsConflict()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync(
            $"/api/v1/traveler/bookings/{seed.PaidWithoutTicketBookingId}/ticket");

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "ticket.not_issued");
    }

    // --- refresh ---

    [Fact]
    public async Task Refresh_ReturnsANewVerifiableQr()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);
        using var first = await client.GetAsync($"/api/v1/traveler/tickets/{seed.ValidTicketId}");
        using var firstBody = await ReadAsync(first, HttpStatusCode.OK);
        var oldContent = firstBody.RootElement.GetProperty("qr").GetProperty("content").GetString();

        using var response = await client.PostAsync(
            $"/api/v1/traveler/tickets/{seed.ValidTicketId}/qr-code/refresh", content: null);

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        var newContent = body.RootElement.GetProperty("qr").GetProperty("content").GetString()!;
        newContent.Should().NotBe(oldContent);
        factory.Services.GetRequiredService<ITicketQrCodec>().TryVerify(newContent, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_OfAUsedTicket_IsConflictCarryingTheStatus()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.PostAsync(
            $"/api/v1/traveler/tickets/{seed.UsedTicketId}/qr-code/refresh", content: null);

        using var body = await ReadAsync(response, HttpStatusCode.Conflict);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be("ticket.not_refreshable");
        body.RootElement.GetProperty("ticketStatus").GetString().Should().Be("Used");
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, "the response body was {0}", payload);
        return JsonDocument.Parse(payload);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        using var body = await ReadAsync(response, status);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}