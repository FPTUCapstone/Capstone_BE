using System.Net;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Bookings;

[Collection(nameof(TripMateApiFactory))]
public sealed class OperatorBookingsEndpointTests
{
    private const string Route = "/api/v1/operator/bookings";

    // ---- authentication and role gate ----

    [Fact]
    public async Task Get_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync(Route + "/1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.Administrator)]
    public async Task Get_NonOperatorRole_ReturnsForbidden(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, role);

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(Route + "/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_OperatorWhoseApplicationIsNotApproved_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = await SeedAsync(factory, approveOperatorA: false);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorAId, UserRole.TourOperator);

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(Route + "/" + seed.BookingOfAId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- list ----

    [Fact]
    public async Task Get_ApprovedOperatorWithNoBookings_ReturnsEmptyPage()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = await SeedAsync(factory, seedBookings: false);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorAId, UserRole.TourOperator);

        HttpResponseMessage response = await client.GetAsync(Route);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement root = body.RootElement;
        root.GetProperty("pageNumber").GetInt32().Should().Be(1);
        root.GetProperty("pageSize").GetInt32().Should().Be(20);
        root.GetProperty("totalCount").GetInt32().Should().Be(0);
        root.GetProperty("totalPages").GetInt32().Should().Be(0);
        root.GetProperty("items").GetArrayLength().Should().Be(0);
        root.GetProperty("summary").GetProperty("totalBookings").GetInt32().Should().Be(0);
        root.GetProperty("summary").GetProperty("totalParticipants").GetInt32().Should().Be(0);
        root.GetProperty("summary").GetProperty("totalConfirmedAmount").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task Get_OperatorSeesOnlyOwnBookingsWithTheSpecifiedContract()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorAId, UserRole.TourOperator);

        HttpResponseMessage response = await client.GetAsync(Route);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement root = body.RootElement;
        root.GetProperty("totalCount").GetInt32().Should().Be(1);
        root.GetProperty("summary").GetProperty("totalBookings").GetInt32().Should().Be(1);
        root.GetProperty("summary").GetProperty("totalParticipants").GetInt32().Should().Be(2);
        root.GetProperty("summary").GetProperty("totalConfirmedAmount").GetDecimal().Should().Be(900_000m);
        JsonElement item = root.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("bookingId").GetString().Should().Be(seed.BookingOfAId.ToString());
        item.GetProperty("bookingCode").GetString().Should().Be("BK-OP-A");
        item.GetProperty("tour").GetProperty("title").GetString().Should().Be("Tour A");
        item.GetProperty("tour").GetProperty("tourId").GetString().Should().Be(seed.TourAId.ToString());
        item.GetProperty("schedule").GetProperty("scheduleId").GetString().Should().NotBeNullOrEmpty();
        item.GetProperty("booker").GetProperty("fullName").GetString().Should().Be("Alice Booker");
        item.GetProperty("booker").GetProperty("email").GetString().Should().Be("alice@example.com");
        item.TryGetProperty("phoneNumber", out _).Should().BeFalse();
        item.GetProperty("booker").TryGetProperty("phoneNumber", out _).Should().BeFalse();
        item.GetProperty("quantity").GetInt32().Should().Be(2);
        item.GetProperty("totalAmount").GetDecimal().Should().Be(900_000m);
        item.GetProperty("discountAmount").GetDecimal().Should().Be(100_000m);
        item.GetProperty("status").GetString().Should().Be("Confirmed");
        item.GetProperty("paymentStatus").GetString().Should().Be("Paid");
        item.GetProperty("checkInStatus").GetString().Should().Be("CheckedIn");
        item.GetProperty("checkedInAtUtc").ValueKind.Should().Be(JsonValueKind.String);
        item.GetProperty("bookedAtUtc").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Get_KeywordAndStatusFilters_AreApplied()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorAId, UserRole.TourOperator);

        using JsonDocument hit = JsonDocument.Parse(await client.GetStringAsync(Route + "?keyword=alice&status=Confirmed"));
        using JsonDocument miss = JsonDocument.Parse(await client.GetStringAsync(Route + "?keyword=zzz"));
        using JsonDocument wrongStatus = JsonDocument.Parse(await client.GetStringAsync(Route + "?status=Cancelled"));

        hit.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
        miss.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
        wrongStatus.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData("?pageSize=101")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageNumber=0")]
    [InlineData("?tourId=0")]
    [InlineData("?scheduleId=-1")]
    public async Task Get_InvalidPagingOrIdFilter_ReturnsBadRequestWithStableErrorCode(string query)
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorAId, UserRole.TourOperator);

        HttpResponseMessage response = await client.GetAsync(Route + query);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("operator_booking.invalid_filter");
    }

    [Fact]
    public async Task Get_InvertedDepartureRange_ReturnsBadRequestWithStableErrorCode()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorAId, UserRole.TourOperator);

        HttpResponseMessage response = await client.GetAsync(Route + "?departureFrom=2026-11-05&departureTo=2026-11-04");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("operator_booking.invalid_date_range");
    }

    [Fact]
    public async Task Get_TourOrScheduleOfAnotherOperator_ReturnsForbiddenWithStableErrorCode()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorAId, UserRole.TourOperator);

        HttpResponseMessage byTour = await client.GetAsync(Route + "?tourId=" + seed.TourBId);
        HttpResponseMessage bySchedule = await client.GetAsync(Route + "?scheduleId=" + seed.ScheduleBId);

        byTour.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await byTour.Content.ReadAsStringAsync()).Should().Contain("operator_booking.forbidden");
        bySchedule.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- detail ----

    [Fact]
    public async Task GetDetail_OwnBooking_ReturnsTheSpecifiedContractWithoutSensitiveData()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorAId, UserRole.TourOperator);

        HttpResponseMessage response = await client.GetAsync(Route + "/" + seed.BookingOfAId);
        string raw = await response.Content.ReadAsStringAsync();
        using JsonDocument body = JsonDocument.Parse(raw);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement root = body.RootElement;
        root.GetProperty("bookingId").GetString().Should().Be(seed.BookingOfAId.ToString());
        root.GetProperty("status").GetString().Should().Be("Confirmed");
        root.GetProperty("schedule").GetProperty("meetingPoint").GetString().Should().Be("Cruise Port");
        root.GetProperty("booker").GetProperty("phoneNumber").GetString().Should().Be("+84901234567");
        root.GetProperty("participants").GetArrayLength().Should().Be(2);
        JsonElement participant = root.GetProperty("participants")[0];
        participant.GetProperty("fullName").GetString().Should().Be("Nguyen Van A");
        participant.GetProperty("participantType").GetString().Should().Be("Adult");
        root.GetProperty("ticket").GetProperty("status").GetString().Should().Be("Used");
        root.GetProperty("checkInStatus").GetString().Should().Be("CheckedIn");
        root.GetProperty("payment").GetProperty("gateway").GetString().Should().Be("PayOS");
        root.GetProperty("payment").GetProperty("status").GetString().Should().Be("Success");
        root.GetProperty("paidAmount").GetDecimal().Should().Be(900_000m);
        root.GetProperty("subtotalAmount").GetDecimal().Should().Be(1_000_000m);
        root.GetProperty("coupon").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("canCancel").GetBoolean().Should().BeFalse();
        root.GetProperty("cancelBlockedReason").GetString().Should().Be("CheckedIn");
        root.TryGetProperty("cancellationWindowExpired", out _).Should().BeFalse();

        foreach (string forbidden in new[]
                 {
                     "idDocument", "nationality", "P1234567", "protected:", "+84900000001", "a@secret.example",
                     "rawResponse", "gateway-secret", "checkoutUrl", "qrCode", "paymentLinkId", "orderCode",
                 })
        {
            raw.Should().NotContainEquivalentOf(forbidden);
        }
    }

    [Fact]
    public async Task GetDetail_BookingOfAnotherOperator_ReturnsForbiddenAndNoBookingData()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorAId, UserRole.TourOperator);

        HttpResponseMessage response = await client.GetAsync(Route + "/" + seed.BookingOfBId);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().Contain("operator_booking.forbidden");
        body.Should().NotContain("BK-OP-B");
        body.Should().NotContain("Bob Booker");
    }

    [Fact]
    public async Task GetDetail_UnknownBooking_ReturnsNotFoundWithStableErrorCode()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorAId, UserRole.TourOperator);

        HttpResponseMessage response = await client.GetAsync(Route + "/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("operator_booking.not_found");
    }

    // ---- seed ----

    /// <summary>
    /// Operator A (and its single Confirmed, checked-in, paid booking by "Alice Booker") versus operator B
    /// (one booking by "Bob Booker").
    /// </summary>
    private static Task<Seed> SeedAsync(
        TripMateApiFactory factory, bool approveOperatorA = true, bool seedBookings = true) =>
        factory.WithDbContextAsync(async db =>
        {
            DateTimeOffset now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
            User alice = NewUser("alice@example.com", "Alice Booker", UserRole.Traveler, now, "+84901234567");
            User bob = NewUser("bob@example.com", "Bob Booker", UserRole.Traveler, now);
            User operatorA = NewUser("operator-a@example.com", "Operator A", UserRole.TourOperator, now);
            User operatorB = NewUser("operator-b@example.com", "Operator B", UserRole.TourOperator, now);
            db.Users.AddRange(alice, bob, operatorA, operatorB);
            await db.SaveChangesAsync();

            OperatorProfile profileA = NewProfile(operatorA, "A", approveOperatorA, now);
            OperatorProfile profileB = NewProfile(operatorB, "B", true, now);
            db.OperatorProfiles.AddRange(profileA, profileB);
            await db.SaveChangesAsync();

            Tour tourA = NewTour(operatorA, profileA, "Tour A");
            Tour tourB = NewTour(operatorB, profileB, "Tour B");
            db.Tours.AddRange(tourA, tourB);
            await db.SaveChangesAsync();

            TourSchedule scheduleA = NewSchedule(tourA, now.AddDays(14), "Cruise Port");
            TourSchedule scheduleB = NewSchedule(tourB, now.AddDays(14), "Other Port");
            db.TourSchedules.AddRange(scheduleA, scheduleB);
            await db.SaveChangesAsync();

            if (!seedBookings)
            {
                return new Seed(operatorA.Id, tourA.Id, tourB.Id, scheduleA.Id, scheduleB.Id, 0, 0);
            }

            var participants = new List<BookingParticipant>
            {
                BookingParticipant.Create("Nguyen Van A", ParticipantType.Adult, "+84900000001", "a@secret.example",
                    BookingIdDocumentType.Passport, "protected:P1234567:cipher", "VNM"),
                BookingParticipant.Create("Nguyen Thi B", ParticipantType.Child, null, null, null, null, null),
            };
            Booking bookingA = Booking.Create("BK-OP-A", alice.Id, scheduleA.Id, null, 1_000_000m, 100_000m,
                900_000m, participants, now, now.AddMinutes(30));
            Set(bookingA, nameof(Booking.Status), BookingStatus.Confirmed);
            Set(bookingA, nameof(Booking.PaymentStatus), PaymentStatus.Paid);
            Booking bookingB = Booking.Create("BK-OP-B", bob.Id, scheduleB.Id, null, 500_000m, 0m, 500_000m,
                [BookingParticipant.Create("Bob Guest", ParticipantType.Adult, null, null, null, null, null)],
                now, now.AddMinutes(30));
            Set(bookingB, nameof(Booking.Status), BookingStatus.Confirmed);
            Set(bookingB, nameof(Booking.PaymentStatus), PaymentStatus.Paid);
            db.Bookings.AddRange(bookingA, bookingB);
            await db.SaveChangesAsync();

            var ticket = (Ticket)Activator.CreateInstance(typeof(Ticket), nonPublic: true)!;
            Set(ticket, nameof(Ticket.BookingId), bookingA.Id);
            Set(ticket, nameof(Ticket.TicketCode), Ticket.CodeFor(bookingA.Id));
            Set(ticket, nameof(Ticket.Status), TicketStatus.Used);
            Set(ticket, nameof(Ticket.IssuedAtUtc), now.AddMinutes(1));
            Set<DateTimeOffset?>(ticket, nameof(Ticket.CheckedInAtUtc), now.AddDays(14));
            db.Tickets.Add(ticket);

            PaymentTransaction payment = PaymentTransaction.Create(bookingA.Id, PaymentGateway.PayOS, 900_000m, 4242,
                now, checkoutUrl: "https://pay.example/checkout/secret", paymentLinkId: "plink-secret",
                qrCode: "qr-secret");
            payment.MarkSuccess("FT26102012345", "{\"raw\":\"gateway-secret\"}", now.AddSeconds(40), 10m);
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();

            return new Seed(operatorA.Id, tourA.Id, tourB.Id, scheduleA.Id, scheduleB.Id, bookingA.Id, bookingB.Id);
        });

    private static User NewUser(string email, string fullName, UserRole role, DateTimeOffset now, string? phone = null) =>
        new()
        {
            Email = email,
            PhoneNumber = phone,
            FullName = fullName,
            Role = role,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

    private static OperatorProfile NewProfile(User user, string suffix, bool approved, DateTimeOffset now) => new()
    {
        UserId = user.Id,
        User = user,
        CompanyName = $"Company {suffix}",
        TaxCode = $"TAX-{suffix}",
        BusinessLicenseNo = $"LIC-{suffix}",
        ApprovalStatus = approved ? OperatorApprovalStatus.Approved : OperatorApprovalStatus.PendingApproval,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private static Tour NewTour(User owner, OperatorProfile profile, string title)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), owner.Id);
        Set(tour, nameof(Tour.OperatorProfile), profile);
        Set(tour, nameof(Tour.Title), title);
        Set(tour, nameof(Tour.BasePrice), 500_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        return tour;
    }

    private static TourSchedule NewSchedule(Tour tour, DateTimeOffset start, string meetingPoint)
    {
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), start);
        Set(schedule, nameof(TourSchedule.EndAtUtc), start.AddHours(8));
        Set(schedule, nameof(TourSchedule.MeetingPoint), meetingPoint);
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.ReservedCapacity), 2);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        return schedule;
    }

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);

    private sealed record Seed(
        long OperatorAId,
        long TourAId,
        long TourBId,
        long ScheduleAId,
        long ScheduleBId,
        long BookingOfAId,
        long BookingOfBId);
}