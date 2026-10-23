using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tours;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateTourPackageEndpointTests
{
    [Fact]
    public async Task Create_WithApprovedOperatorAndSameIdempotencyKey_ReplaysTheOriginalDraft()
    {
        using var factory = new TripMateApiFactory();
        var (poiId, destinationId, policyTemplateId) = await SeedOperatorAndPoiAsync(factory, 71);
        using var client = factory.CreateAuthenticatedClient(71, UserRole.TourOperator);
        var key = Guid.NewGuid().ToString();
        var request = ValidRequest(poiId, destinationId, policyTemplateId);
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        using var first = await client.PostAsJsonAsync("/api/v1/operator/tours", request);
        using var second = await client.PostAsJsonAsync("/api/v1/operator/tours", request);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstId = ReadTourId(await first.Content.ReadAsStringAsync());
        ReadTourId(await second.Content.ReadAsStringAsync()).Should().Be(firstId);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Tours.CountAsync()).Should().Be(1);
            (await db.TourPackageCreateOperations.CountAsync()).Should().Be(1);
            return true;
        });
    }

    private static long ReadTourId(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("tourId").GetInt64();
    }

    private static object ValidRequest(long poiId, long destinationId, long policyTemplateId) => new
    {
        title = "Integration tour",
        description = "A complete commercial tour draft.",
        basePrice = 1000000m,
        durationDays = 1,
        destinationIds = new[] { destinationId },
        cancellationPolicyTemplateId = policyTemplateId,
        itineraryItems = new[] { new { dayNo = 1, sequenceNo = 1, poiId, stayDurationMinutes = 60 } },
        schedules = new[] { new { startAtUtc = DateTimeOffset.UtcNow.AddDays(7), endAtUtc = DateTimeOffset.UtcNow.AddDays(8), totalCapacity = 12, minimumCapacity = 2, adultPrice = 1000000m } },
    };

    private static Task<(long PoiId, long DestinationId, long PolicyTemplateId)> SeedOperatorAndPoiAsync(TripMateApiFactory factory, long operatorId) =>
        factory.WithDbContextAsync(async db =>
        {
            var user = new User { Id = operatorId, Email = $"operator-{operatorId}@test.local", FullName = "Operator", Role = UserRole.TourOperator, Status = AccountStatus.Active };
            db.Users.Add(user);
            db.OperatorProfiles.Add(new OperatorProfile { UserId = operatorId, User = user, CompanyName = "Integration Tours", TaxCode = $"TAX{operatorId}", BusinessLicenseNo = $"LIC{operatorId}", ApprovalStatus = OperatorApprovalStatus.Approved });
            var category = PoiCategory.Create("Integration attraction", null);
            db.PoiCategories.Add(category);
            var poi = PointOfInterest.Create(category, "Integration POI", 21.03m, 105.85m, operatorId, DateTimeOffset.UtcNow);
            db.PointsOfInterest.Add(poi);
            var destination = (Destination)Activator.CreateInstance(typeof(Destination), nonPublic: true)!;
            typeof(Destination).GetProperty(nameof(Destination.Name))!.SetValue(destination, "Integration region");
            db.Destinations.Add(destination);
            var policy = CancellationPolicyTemplate.Create("Integration policy", null, DateTimeOffset.UtcNow,
                [CancellationPolicyTemplateRule.Create(30, 0), CancellationPolicyTemplateRule.Create(0, 100)]);
            db.CancellationPolicyTemplates.Add(policy);
            await db.SaveChangesAsync();
            return (poi.Id, destination.Id, policy.Id);
        });
}