using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Emergency;

[Collection(nameof(TripMateApiFactory))]
public sealed class Uc70RecoveryAndEvidenceEndpointTests
{
    private const string NoticesPath = "/api/v1/traveler/tour-bookings/emergency-notices";
    private const long OperatorId = 801;
    private const long ForeignOperatorId = 802;
    private const long TravelerId = 803;
    private const long ForeignTravelerId = 804;
    private static readonly Regex MoneyWords = new(
        "refund|settlement|payout|commission|manualreview",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

    private static TripMateApiFactory CreateFactory() => new(
        dateTimeProviderFactory: _ => new FixedClock(Now));

    private static string DeparturePath(Seed seed) =>
        $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

    [Fact]
    public async Task Notices_FindTheAffectedBookingWithoutAnyPush_NewestFirst()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        var later = await AddDepartureAsync(factory, seed.TourId, Now.AddDays(6));
        await CancelAsync(factory, seed.TourId, seed.ScheduleId, Now);
        await CancelAsync(factory, seed.TourId, later.ScheduleId, Now);
        using var traveler = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);

        using var response = await traveler.GetAsync(NoticesPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.EnumerateArray().ToArray();
        items.Select(item => item.GetProperty("bookingId").GetInt64())
            .Should().BeEquivalentTo([seed.BookingId, later.BookingId]);
        var first = items[0];
        first.GetProperty("action").GetString().Should().Be("CancelBeforeDeparture");
        first.GetProperty("requiresAcknowledgement").GetBoolean().Should().BeTrue();
        first.TryGetProperty("committedAtUtc", out _).Should().BeTrue();
        first.TryGetProperty("scheduleId", out _).Should().BeTrue();
        string raw = json.RootElement.GetRawText();
        foreach (var hidden in new[] { "reasonDetails", "evidence", "immediateInstructions", "actorUserId", "refund" })
        {
            raw.Should().NotContainEquivalentOf(hidden);
        }
    }

    [Fact]
    public async Task Notices_AcknowledgedStaysListedWithoutRequiringAcknowledgement()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        await CancelAsync(factory, seed.TourId, seed.ScheduleId, Now);
        using var traveler = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);

        (await traveler.PostAsync(
                $"/api/v1/traveler/tour-bookings/{seed.BookingId}/emergency-status/acknowledge", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await traveler.GetAsync(NoticesPath);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = json.RootElement.EnumerateArray().Single();
        item.GetProperty("requiresAcknowledgement").GetBoolean().Should().BeFalse();
        item.GetProperty("acknowledgedAtUtc").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Notices_OrdinaryCancellationHasNoNotice()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        await factory.WithDbContextAsync(async db =>
        {
            var booking = await db.Bookings.FindAsync(seed.BookingId);
            Set(booking!, nameof(Booking.Status), BookingStatus.Cancelled);
            await db.SaveChangesAsync();
            return true;
        });
        using var traveler = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);

        using var response = await traveler.GetAsync(NoticesPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task EarlierOrdinaryCancellation_IsNotTreatedAsAffectedByALaterDecision()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        await CancelAsync(factory, seed.TourId, seed.ScheduleId, Now);
        long earlierBookingId = await factory.WithDbContextAsync(async db =>
        {
            var booking = Booking.Create(
                TravelerId, seed.ScheduleId, 1, 100_000m, BookingStatus.Cancelled, PaymentStatus.Unpaid);
            Set(booking, nameof(Booking.BookingCode), "UC70EARLIER");
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            return booking.Id;
        });
        using var traveler = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);
        string path = $"/api/v1/traveler/tour-bookings/{earlierBookingId}/emergency-status";

        (await traveler.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await traveler.PostAsync($"{path}/acknowledge", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EvidenceUpdate_WithoutTheVersionItRead_IsRefusedAndChangesNothing()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        await CancelAsync(factory, seed.TourId, seed.ScheduleId, Now, evidenceReference: "REF-1");
        using var client = factory.CreateAuthenticatedClient(OperatorId, UserRole.TourOperator);
        var path = $"{DeparturePath(seed)}/emergency-evidence";

        using var save = await client.PutAsJsonAsync(path, new { evidenceReference = "REF-2" });
        using var withdraw = await client.PutAsJsonAsync(path, new { withdrawEvidence = true });

        save.StatusCode.Should().Be(HttpStatusCode.Conflict);
        withdraw.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync(await client.GetAsync(path))).GetProperty("evidenceReference").GetString()
            .Should().Be("REF-1");
    }

    [Fact]
    public async Task Notices_ForeignTravelerSeesNothingAndOperatorIsForbidden()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        await CancelAsync(factory, seed.TourId, seed.ScheduleId, Now);
        using var foreign = factory.CreateAuthenticatedClient(ForeignTravelerId, UserRole.Traveler);
        using var operatorClient = factory.CreateAuthenticatedClient(OperatorId, UserRole.TourOperator);

        using var foreignResponse = await foreign.GetAsync(NoticesPath);
        using var json = JsonDocument.Parse(await foreignResponse.Content.ReadAsStringAsync());
        json.RootElement.GetArrayLength().Should().Be(0);
        (await operatorClient.GetAsync(NoticesPath)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EmergencyStoppedBooking_ListsInTripHistoryAndKeepsItsNotice()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        var active = await AddDepartureAsync(factory, seed.TourId, Now.AddHours(-1));
        using var operatorClient = factory.CreateAuthenticatedClient(OperatorId, UserRole.TourOperator);
        string path = $"/api/v1/operator/tours/{seed.TourId}/schedules/{active.ScheduleId}";
        var impact = await ReadAsync(await operatorClient.GetAsync($"{path}/emergency-impact"));
        operatorClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var stop = await operatorClient.PostAsJsonAsync($"{path}/emergency-stop", new
        {
            impactVersion = impact.GetProperty("impactVersion").GetString(),
            currentPassword = "Uc70-recovery-fixture",
            reason = "SafetyIncident",
            reasonDetails = "Road closed",
            immediateInstructions = "Stay at the shelter.",
            emergencyContact = "0900000000",
        });
        stop.StatusCode.Should().Be(HttpStatusCode.OK);
        using var traveler = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);

        using var history = await traveler.GetAsync("/api/v1/traveler/trips?tab=Cancelled");

        history.StatusCode.Should().Be(HttpStatusCode.OK);
        (await history.Content.ReadAsStringAsync()).Should().Contain(nameof(BookingStatus.Interrupted));
        using var upcoming = await traveler.GetAsync("/api/v1/traveler/trips?tab=Upcoming");
        upcoming.StatusCode.Should().Be(HttpStatusCode.OK);
        using var notices = await traveler.GetAsync(NoticesPath);
        using var json = JsonDocument.Parse(await notices.Content.ReadAsStringAsync());
        json.RootElement.EnumerateArray().Single().GetProperty("bookingId").GetInt64()
            .Should().Be(active.BookingId);
        json.RootElement.EnumerateArray().Single().GetProperty("action").GetString()
            .Should().Be("EmergencyStopActive");
    }

    [Fact]
    public async Task Evidence_IsReadThenUpdatedWithoutLosingUntouchedFields()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        await CancelAsync(factory, seed.TourId, seed.ScheduleId, Now, evidenceReference: "REF-1");
        using var client = factory.CreateAuthenticatedClient(OperatorId, UserRole.TourOperator);
        var path = $"{DeparturePath(seed)}/emergency-evidence";

        var current = await ReadAsync(await client.GetAsync(path));
        current.GetProperty("evidenceReference").GetString().Should().Be("REF-1");
        current.GetProperty("evidenceUrl").ValueKind.Should().Be(JsonValueKind.Null);

        using var put = await client.PutAsJsonAsync(path, new
        {
            evidenceUrl = "https://authority.example/notice",
            evidenceReference = current.GetProperty("evidenceReference").GetString(),
            authorityReference = (string?)null,
            expectedVersion = current.GetProperty("evidenceVersion").GetString(),
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await ReadAsync(await client.GetAsync(path));
        updated.GetProperty("evidenceReference").GetString().Should().Be("REF-1");
        updated.GetProperty("evidenceUrl").GetString().Should().Be("https://authority.example/notice");
        updated.GetProperty("evidenceVersion").GetString()
            .Should().NotBe(current.GetProperty("evidenceVersion").GetString());
    }

    [Fact]
    public async Task Evidence_StaleVersionIsRejectedAndChangesNothing()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        await CancelAsync(factory, seed.TourId, seed.ScheduleId, Now, evidenceReference: "REF-1");
        using var client = factory.CreateAuthenticatedClient(OperatorId, UserRole.TourOperator);
        var path = $"{DeparturePath(seed)}/emergency-evidence";

        using var put = await client.PutAsJsonAsync(path, new
        {
            evidenceReference = "REF-2",
            expectedVersion = new string('0', 64),
        });

        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await put.Content.ReadAsStringAsync()).Should().Contain("tour_schedule.emergency_evidence_stale");
        (await ReadAsync(await client.GetAsync(path)))
            .GetProperty("evidenceReference").GetString().Should().Be("REF-1");
    }

    [Fact]
    public async Task Evidence_WithdrawClearsEveryField()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        await CancelAsync(factory, seed.TourId, seed.ScheduleId, Now, evidenceReference: "REF-1");
        using var client = factory.CreateAuthenticatedClient(OperatorId, UserRole.TourOperator);
        var path = $"{DeparturePath(seed)}/emergency-evidence";

        var read = await ReadAsync(await client.GetAsync(path));
        (await client.PutAsJsonAsync(path, new
        {
            withdrawEvidence = true,
            expectedVersion = read.GetProperty("evidenceVersion").GetString(),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await ReadAsync(await client.GetAsync(path));
        after.GetProperty("evidenceReference").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Evidence_ForeignOperatorAndUndecidedDepartureAreSafe404()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        using var owner = factory.CreateAuthenticatedClient(OperatorId, UserRole.TourOperator);
        using var foreign = factory.CreateAuthenticatedClient(ForeignOperatorId, UserRole.TourOperator);
        var path = $"{DeparturePath(seed)}/emergency-evidence";

        (await owner.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await CancelAsync(factory, seed.TourId, seed.ScheduleId, Now);
        (await foreign.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Cancel_OfAPaidBooking_ChangesOnlyStatesAndTouchesNoMoney()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        await PayAsync(factory, seed.BookingId);
        using var client = factory.CreateAuthenticatedClient(OperatorId, UserRole.TourOperator);
        var impact = await ReadAsync(await client.GetAsync($"{DeparturePath(seed)}/emergency-impact"));
        AssertNoMoneyFields(impact);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync($"{DeparturePath(seed)}/cancel-before-departure", new
        {
            impactVersion = impact.GetProperty("impactVersion").GetString(),
            reason = "WeatherEmergency",
            reasonDetails = "A verified storm makes departure unsafe.",
            emergencyContact = "0900000000",
        });

        AssertNoMoneyFields(await ReadAsync(response));
        await AssertStatesAsync(factory, seed.ScheduleId, seed.BookingId,
            TourScheduleStatus.Cancelled, BookingStatus.Cancelled);
    }

    [Fact]
    public async Task Stop_OfAPaidBooking_ChangesOnlyStatesAndTouchesNoMoney()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        var active = await AddDepartureAsync(factory, seed.TourId, Now.AddHours(-1));
        await PayAsync(factory, active.BookingId);
        string path = $"/api/v1/operator/tours/{seed.TourId}/schedules/{active.ScheduleId}";
        using var client = factory.CreateAuthenticatedClient(OperatorId, UserRole.TourOperator);
        var impact = await ReadAsync(await client.GetAsync($"{path}/emergency-impact"));
        AssertNoMoneyFields(impact);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync($"{path}/emergency-stop", new
        {
            impactVersion = impact.GetProperty("impactVersion").GetString(),
            currentPassword = "Uc70-recovery-fixture",
            reason = "WeatherEmergency",
            reasonDetails = "A verified storm makes the route unsafe.",
            immediateInstructions = "Stay with the guide at the shelter.",
            emergencyContact = "0900000000",
        });

        AssertNoMoneyFields(await ReadAsync(response));
        await AssertStatesAsync(factory, active.ScheduleId, active.BookingId,
            TourScheduleStatus.EmergencyStopped, BookingStatus.Interrupted);
    }

    [Fact]
    public async Task TravelerStatus_CarriesNoRefundResult()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory);
        await PayAsync(factory, seed.BookingId);
        await CancelAsync(factory, seed.TourId, seed.ScheduleId, Now);
        using var traveler = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);

        var status = await ReadAsync(await traveler.GetAsync(
            $"/api/v1/traveler/tour-bookings/{seed.BookingId}/emergency-status"));

        AssertNoMoneyFields(status);
    }

    private static Task PayAsync(TripMateApiFactory factory, long bookingId) =>
        factory.WithDbContextAsync(async db =>
        {
            var booking = await db.Bookings.SingleAsync(item => item.Id == bookingId);
            Set(booking, nameof(Booking.PaymentStatus), PaymentStatus.Paid);
            var payment = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, 100_000m, 70_000 + (int)bookingId, Now);
            payment.MarkSuccess($"uc70-payment-{bookingId}", null, Now, 0m);
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();
            return true;
        });

    private static Task AssertStatesAsync(
        TripMateApiFactory factory,
        long scheduleId,
        long bookingId,
        TourScheduleStatus scheduleStatus,
        BookingStatus bookingStatus) =>
        factory.WithDbContextAsync(async db =>
        {
            (await db.TourSchedules.SingleAsync(item => item.Id == scheduleId)).Status.Should().Be(scheduleStatus);
            var booking = await db.Bookings.SingleAsync(item => item.Id == bookingId);
            booking.Status.Should().Be(bookingStatus);
            booking.PaymentStatus.Should().Be(PaymentStatus.Paid, "payment state belongs to the refund workflow");
            (await db.Refunds.CountAsync()).Should().Be(0);
            (await db.RefundManualCompletionOperations.CountAsync()).Should().Be(0);
            var payments = await db.PaymentTransactions.Where(item => item.BookingId == bookingId).ToListAsync();
            payments.Should().ContainSingle().Which.TransactionType.Should().Be(PaymentTransaction.TypePayment);
            (await db.Payouts.CountAsync()).Should().Be(0);
            (await db.PayoutItems.CountAsync()).Should().Be(0);
            (await db.PayoutRefundItems.CountAsync()).Should().Be(0);
            (await db.PayoutAdjustments.CountAsync()).Should().Be(0);
            var audit = await db.AuditLogs.Where(item => item.AffectedEntityId == scheduleId).ToListAsync();
            audit.Should().NotBeEmpty();
            audit.Should().OnlyContain(item => !MoneyWords.IsMatch(item.AfterData ?? string.Empty));
            return true;
        });

    private static void AssertNoMoneyFields(JsonElement json)
    {
        MoneyWords.IsMatch(json.GetRawText()).Should().BeFalse("UC-70 returns no refund, payout or commission result");
    }

    private static async Task CancelAsync(
        TripMateApiFactory factory,
        long tourId,
        long scheduleId,
        DateTimeOffset _,
        string? evidenceReference = null)
    {
        using var client = factory.CreateAuthenticatedClient(OperatorId, UserRole.TourOperator);
        string path = $"/api/v1/operator/tours/{tourId}/schedules/{scheduleId}";
        var impact = await ReadAsync(await client.GetAsync($"{path}/emergency-impact"));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var cancel = await client.PostAsJsonAsync($"{path}/cancel-before-departure", new
        {
            impactVersion = impact.GetProperty("impactVersion").GetString(),
            reason = "WeatherEmergency",
            reasonDetails = "A verified storm makes departure unsafe.",
            emergencyContact = "0900000000",
            evidenceReference,
        });
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Task<Seed> SeedAsync(TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async db =>
        {
            var hasher = new PasswordHasherService();
            var operatorUser = NewUser(OperatorId, UserRole.TourOperator, hasher);
            var foreignOperator = NewUser(ForeignOperatorId, UserRole.TourOperator, hasher);
            db.Users.AddRange(
                operatorUser,
                foreignOperator,
                NewUser(TravelerId, UserRole.Traveler, hasher),
                NewUser(ForeignTravelerId, UserRole.Traveler, hasher));
            foreach (var user in new[] { operatorUser, foreignOperator })
            {
                db.OperatorProfiles.Add(new OperatorProfile
                {
                    UserId = user.Id,
                    User = user,
                    CompanyName = $"Operator {user.Id}",
                    TaxCode = $"TAX-{user.Id}",
                    BusinessLicenseNo = $"LICENSE-{user.Id}",
                    ApprovalStatus = OperatorApprovalStatus.Approved,
                });
            }

            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            Set(tour, nameof(Tour.OperatorUserId), OperatorId);
            Set(tour, nameof(Tour.Title), "UC70 recovery tour");
            Set(tour, nameof(Tour.BasePrice), 100_000m);
            Set(tour, nameof(Tour.DurationDays), 1);
            Set(tour, nameof(Tour.Status), TourStatus.Published);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();
            var (scheduleId, bookingId) = await AddScheduleAsync(db, tour, Now.AddDays(4));
            return new Seed(tour.Id, scheduleId, bookingId);
        });

    private static Task<(long ScheduleId, long BookingId)> AddDepartureAsync(
        TripMateApiFactory factory,
        long tourId,
        DateTimeOffset start) =>
        factory.WithDbContextAsync(async db =>
        {
            var tour = await db.Tours.FindAsync(tourId);
            return await AddScheduleAsync(db, tour!, start);
        });

    private static async Task<(long ScheduleId, long BookingId)> AddScheduleAsync(
        TestApiDbContext db,
        Tour tour,
        DateTimeOffset start)
    {
        var schedule = TourSchedule.Create(
            start, start.AddHours(8), "Hue", 20, Now.AddDays(-5), adultPrice: 100_000m);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        schedule.ReserveCapacity(1);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var booking = Booking.Create(
            TravelerId, schedule.Id, 1, 100_000m, BookingStatus.Confirmed, PaymentStatus.Unpaid);
        Set(booking, nameof(Booking.TourSchedule), schedule);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return (schedule.Id, booking.Id);
    }

    private static User NewUser(long id, UserRole role, PasswordHasherService hasher) => new()
    {
        Id = id,
        Email = $"uc70-recovery-{id}@test.local",
        FullName = $"UC70 {id}",
        Role = role,
        Status = AccountStatus.Active,
        PasswordHash = hasher.Hash("Uc70-recovery-fixture"),
    };

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(
            property,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed record Seed(long TourId, long ScheduleId, long BookingId);

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
