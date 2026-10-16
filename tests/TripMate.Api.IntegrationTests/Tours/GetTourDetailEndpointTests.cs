using System.Net;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tours;

/// <summary>UC-26 HTTP contract on the in-memory host. SQL-specific behaviour lives in the SQL Server suite.</summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class GetTourDetailEndpointTests
{
    [Fact]
    public async Task GetDetail_AsGuest_ReturnsTheExactPublicContractAndNoStore()
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedPublishedTourAsync(factory, 401);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tours/{tourId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "tourId", "title", "description", "destination", "destinations", "durationDays", "basePrice",
            "currency", "operator", "images", "inclusions", "exclusions", "cancellationPolicy", "itinerary",
            "schedules", "hasBookableSchedule", "rating", "asOfUtc");
        root.GetProperty("tourId").GetString().Should().Be(tourId.ToString());
        root.GetProperty("operator").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("name", "contactPhone");
        root.GetProperty("schedules")[0].EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("scheduleId", "startAtUtc", "endAtUtc", "meetingPoint",
                "registrationDeadlineAtUtc", "adultPrice", "childPrice", "infantPrice", "remainingSlots",
                "availabilityStatus", "bookable");
        root.GetProperty("rating").GetProperty("count").GetInt32().Should().Be(0);
        root.GetProperty("rating").GetProperty("average").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.ToString().Should().NotContain("TAX-401").And.NotContain("LIC-401");
    }

    [Fact]
    public async Task GetDetail_WithATravelerSession_StillWorks()
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedPublishedTourAsync(factory, 402);
        using var client = factory.CreateAuthenticatedClient(900, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/tours/{tourId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("987654321")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public async Task GetDetail_ForAnUnknownOrInvalidId_ReturnsNotFound(string id)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tours/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDetail_ForADraft_ReturnsTheSameNotFoundAsAMissingTour()
    {
        await using var factory = new TripMateApiFactory();
        var draftId = await SeedPublishedTourAsync(factory, 403, publish: false);
        using var client = factory.CreateClient();

        using var draft = await client.GetAsync($"/api/v1/tours/{draftId}");
        using var missing = await client.GetAsync("/api/v1/tours/987654321");

        draft.StatusCode.Should().Be(HttpStatusCode.NotFound);
        draft.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        draft.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var draftBody = JsonDocument.Parse(await draft.Content.ReadAsStringAsync());
        using var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        draftBody.RootElement.GetProperty("errorCode").GetString().Should().Be("tour.not_found");
        draftBody.RootElement.GetProperty("title").GetString()
            .Should().Be(missingBody.RootElement.GetProperty("title").GetString());
        draftBody.RootElement.GetProperty("status").GetInt32()
            .Should().Be(missingBody.RootElement.GetProperty("status").GetInt32());
        draftBody.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(missingBody.RootElement.GetProperty("errorCode").GetString());
    }

    private static Task<long> SeedPublishedTourAsync(TripMateApiFactory factory, long operatorId, bool publish = true) =>
        factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Id = operatorId,
                Email = $"operator-{operatorId}@test.local",
                FullName = "Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
            };
            db.Users.Add(user);
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = operatorId,
                User = user,
                CompanyName = "Detail Tours",
                TaxCode = $"TAX-{operatorId}",
                BusinessLicenseNo = $"LIC-{operatorId}",
                ContactPhone = "0900000000",
                ContactAddress = "Private address",
                ApprovalStatus = OperatorApprovalStatus.Approved,
            });
            var category = PoiCategory.Create("Detail attraction", null);
            db.PoiCategories.Add(category);
            var poi = PointOfInterest.Create(category, "Detail POI", 15.88m, 108.33m, operatorId, DateTimeOffset.UtcNow);
            db.PointsOfInterest.Add(poi);
            var destination = (Destination)Activator.CreateInstance(typeof(Destination), nonPublic: true)!;
            typeof(Destination).GetProperty(nameof(Destination.Name))!.SetValue(destination, "Hoi An");
            db.Destinations.Add(destination);
            var policy = CancellationPolicyTemplate.Create("Detail policy", null, DateTimeOffset.UtcNow,
                [CancellationPolicyTemplateRule.Create(30, 0), CancellationPolicyTemplateRule.Create(0, 100)]);
            db.CancellationPolicyTemplates.Add(policy);
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            var tour = Tour.CreateDraft(
                operatorId,
                categoryId: null,
                "Detail tour",
                "A tour used by the UC-26 endpoint test.",
                1_000_000m,
                durationDays: 1,
                destinationIds: [destination.Id],
                destinationSummary: "Hoi An",
                policy,
                [TourItineraryItem.Create(1, 1, poi.Id, null, 60, null)],
                [TourSchedule.Create(now.AddDays(20), now.AddDays(21), "Lobby", 10, now, adultPrice: 1_000_000m)],
                now,
                "Meals",
                null);
            if (publish)
            {
                tour.SubmitForReview(now);
                tour.Approve(2, now);
                tour.Publish(2, now);
            }

            db.Tours.Add(tour);
            await db.SaveChangesAsync();
            return tour.Id;
        });
}