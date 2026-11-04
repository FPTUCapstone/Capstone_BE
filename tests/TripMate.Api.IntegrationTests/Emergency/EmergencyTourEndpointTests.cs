using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Emergency;

[Collection(nameof(TripMateApiFactory))]
public sealed class EmergencyTourEndpointTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private const string OperatorPassword = "OperatorPass123!";

    [Fact]
    public async Task CancelBeforeDeparture_ThenTravelerReadsAndAcknowledgesPersistedStatus()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddDays(8));
        using var operatorClient = factory.CreateAuthenticatedClient(
            seed.OperatorId, UserRole.TourOperator);
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var previewResponse = await operatorClient.GetAsync($"{operatorBase}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        operatorClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var cancelResponse = await operatorClient.PostAsJsonAsync(
            $"{operatorBase}/cancel-before-departure",
            new
            {
                impactVersion,
                reason = "WeatherEmergency",
                reasonDetails = "A verified severe storm makes departure unsafe.",
                emergencyContact = "0900000000",
                evidenceReference = "official-bulletin-123",
            });

        using var travelerClient = factory.CreateAuthenticatedClient(
            seed.TravelerId, UserRole.Traveler);
        string travelerEndpoint =
            $"/api/v1/traveler/tour-bookings/{seed.BookingId}/emergency-status";
        using var statusResponse = await travelerClient.GetAsync(travelerEndpoint);
        using var acknowledgeResponse = await travelerClient.PostAsync(
            $"{travelerEndpoint}/acknowledge", content: null);

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        cancelResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        acknowledgeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPropertyAsync(statusResponse, "bookingStatus"))
            .Should().Be(nameof(BookingStatus.Cancelled));
        (await factory.WithDbContextAsync(db => db.Bookings
            .Where(booking => booking.Id == seed.BookingId)
            .Select(booking => booking.EmergencyNoticeAcknowledgedAtUtc)
            .SingleAsync())).Should().Be(Now);
    }

    [Fact]
    public async Task EmergencyStop_AddEvidenceLater_ThenOperatorCanSupplementEvidence()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(
            seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";
        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var stopResponse = await client.PostAsJsonAsync(
            $"{endpoint}/emergency-stop",
            new
            {
                impactVersion,
                currentPassword = OperatorPassword,
                reason = "AuthorityDirective",
                reasonDetails = "Civil defense ordered an immediate evacuation.",
                immediateInstructions = "Stay with the guide and move to the safe assembly point.",
                emergencyContact = "0900000000",
                addEvidenceLater = true,
            });
        using var evidenceResponse = await client.PutAsJsonAsync(
            $"{endpoint}/emergency-evidence",
            new
            {
                evidenceUrl = "https://authority.example.test/orders/123",
                authorityReference = "EVAC-123",
                expectedVersion = await ReadEvidenceVersionAsync(client, $"{endpoint}/emergency-evidence"),
            });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        evidenceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            var operation = await db.EmergencyTourCancellationOperations.SingleAsync();
            operation.EvidenceAddedLater.Should().BeFalse();
            operation.EvidenceProvenance.Should().Be(
                EmergencyEvidenceProvenance.OperatorProvided);
            operation.EvidenceUrl.Should().Be(
                "https://authority.example.test/orders/123");
            return true;
        });
    }

    private static TripMateApiFactory CreateFactory() => new(
        dateTimeProviderFactory: _ => new FixedDateTimeProvider(Now));

    private static async Task<string> ReadPropertyAsync(
        HttpResponseMessage response,
        string propertyName)
    {
        string payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty(propertyName).GetString()!;
    }

    private static Task<Seed> SeedAsync(TripMateApiFactory factory, DateTimeOffset start) =>
        factory.WithDbContextAsync(async db =>
        {
            const long operatorId = 701;
            const long travelerId = 702;
            var hasher = new PasswordHasherService();
            var operatorUser = new User
            {
                Id = operatorId,
                Email = "uc70-operator@test.local",
                FullName = "UC70 Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
                PasswordHash = hasher.Hash(OperatorPassword),
            };
            db.Users.AddRange(operatorUser, new User
            {
                Id = travelerId,
                Email = "uc70-traveler@test.local",
                FullName = "UC70 Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
            });
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = operatorId,
                User = operatorUser,
                CompanyName = "UC70 Tours",
                TaxCode = "UC70-TAX",
                BusinessLicenseNo = "UC70-LICENSE",
                ApprovalStatus = OperatorApprovalStatus.Approved,
            });
            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            Set(tour, nameof(Tour.OperatorUserId), operatorId);
            Set(tour, nameof(Tour.Title), "UC70 emergency tour");
            Set(tour, nameof(Tour.BasePrice), 100_000m);
            Set(tour, nameof(Tour.DurationDays), 1);
            Set(tour, nameof(Tour.Status), TourStatus.Published);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();

            var schedule = TourSchedule.Create(
                start, start.AddHours(8), "Da Nang", 20, Now,
                adultPrice: 100_000m);
            Set(schedule, nameof(TourSchedule.TourId), tour.Id);
            Set(schedule, nameof(TourSchedule.Tour), tour);
            schedule.ReserveCapacity(1);
            db.TourSchedules.Add(schedule);
            await db.SaveChangesAsync();

            var booking = Booking.Create(
                travelerId, schedule.Id, 1, 100_000m,
                BookingStatus.Confirmed, PaymentStatus.Paid);
            Set(booking, nameof(Booking.TourSchedule), schedule);
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            var payment = PaymentTransaction.Create(
                booking.Id, PaymentGateway.PayOS, 100_000m, 1234, Now);
            payment.MarkSuccess($"paid-{booking.Id}", null, Now, 0m);
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();
            return new Seed(operatorId, travelerId, tour.Id, schedule.Id, booking.Id);
        });

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(
            property,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed record Seed(
        long OperatorId,
        long TravelerId,
        long TourId,
        long ScheduleId,
        long BookingId);

    private sealed class FixedDateTimeProvider(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static async Task<string> ReadEvidenceVersionAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement.TryGetProperty("data", out var data) ? data : body.RootElement;
        return root.GetProperty("evidenceVersion").GetString()!;
    }
}
