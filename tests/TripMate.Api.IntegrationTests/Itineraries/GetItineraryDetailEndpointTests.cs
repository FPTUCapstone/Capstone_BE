using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Itineraries;

[Collection(nameof(TripMateApiFactory))]
public sealed class GetItineraryDetailEndpointTests
{
    [Fact]
    public async Task Get_OwnerReceivesPersistedDetail()
    {
        var saveInterceptor = new CountingSaveChangesInterceptor();
        var explanationProvider = new CountingExplanationProvider();
        await using var factory = new TripMateApiFactory(
            saveChangesInterceptor: saveInterceptor,
            explanationProviderFactory: _ => explanationProvider,
            explanationProviderEnabled: true);
        var itineraryId = await SeedAsync(factory);
        saveInterceptor.Reset();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await client.GetAsync($"/api/v1/itineraries/{itineraryId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Endpoint Trip");
        body.Should().Contain("\"canManage\":true");
        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(3);
        items[0].GetProperty("isMandatory").GetBoolean().Should().BeTrue();
        items[0].GetProperty("recommendationReason").GetString().Should().Be("Mandatory location");
        items[0].GetProperty("friendlyExplanation").GetString()
            .Should().Be("Giải thích cho điểm bắt buộc.");
        items[0].GetProperty("latitude").GetDecimal().Should().Be(16.003300m);
        items[0].GetProperty("longitude").GetDecimal().Should().Be(108.263500m);
        items[1].GetProperty("isMandatory").GetBoolean().Should().BeFalse();
        items[1].GetProperty("friendlyExplanation").GetString()
            .Should().Be("Giải thích cho điểm đề xuất.");
        items[1].GetProperty("latitude").GetDecimal().Should().Be(0m);
        items[1].GetProperty("longitude").GetDecimal().Should().Be(0m);
        items[2].GetProperty("kind").GetString().Should().Be(nameof(ItineraryItemKind.Rest));
        items[2].GetProperty("friendlyExplanation").ValueKind.Should().Be(JsonValueKind.Null);
        items[2].GetProperty("latitude").ValueKind.Should().Be(JsonValueKind.Null);
        items[2].GetProperty("longitude").ValueKind.Should().Be(JsonValueKind.Null);
        saveInterceptor.SaveCalls.Should().Be(0);
        explanationProvider.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Get_NonOwnerWithoutGroupMembershipReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        var itineraryId = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Traveler);

        var response = await client.GetAsync($"/api/v1/itineraries/{itineraryId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_UnknownIdReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/itineraries/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_HistoricalIdReturnsFriendlyExplanationFromCurrentVersion()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedVersionedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/itineraries/{seed.HistoricalId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("itineraryId").GetInt64().Should().Be(seed.CurrentId);
        json.RootElement.GetProperty("version").GetInt32().Should().Be(2);
        var item = json.RootElement.GetProperty("items")[0];
        item.GetProperty("friendlyExplanation").GetString()
            .Should().Be("Giải thích của phiên bản hiện tại.");
        item.GetProperty("friendlyExplanation").GetString()
            .Should().NotBe("Giải thích của phiên bản cũ.");
    }

    private static async Task<long> SeedAsync(TripMateApiFactory factory)
    {
        return await factory.WithDbContextAsync(async context =>
        {
            var now = DateTimeOffset.UtcNow;
            context.Users.Add(new User
            {
                Id = 1,
                Email = "itinerary-owner@example.com",
                FullName = "Itinerary Owner",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            var category = PoiCategory.Create("Attraction", null);
            context.PoiCategories.Add(category);
            await context.SaveChangesAsync();
            var mandatoryPoi = PointOfInterest.Create(
                category,
                "Marble Mountains",
                16.003300m,
                108.263500m,
                1,
                now);
            var zeroCoordinatePoi = PointOfInterest.Create(
                category,
                "Zero Island",
                0m,
                0m,
                1,
                now);
            context.PointsOfInterest.AddRange(mandatoryPoi, zeroCoordinatePoi);
            await context.SaveChangesAsync();
            var itinerary = Itinerary.CreateManual(1, "Endpoint Trip", Itinerary.ActiveStatus, now);
            var mandatory = ItineraryItem.CreateVisit(
                1,
                mandatoryPoi.Id,
                now,
                now.AddHours(1),
                true,
                10_000m,
                "Mandatory location");
            mandatory.AttachFriendlyExplanation("Giải thích cho điểm bắt buộc.");
            itinerary.AddItem(mandatory);
            var optional = ItineraryItem.CreateVisit(
                2,
                zeroCoordinatePoi.Id,
                now.AddHours(1),
                now.AddHours(2),
                false,
                20_000m,
                "Suggested nearby location");
            optional.AttachFriendlyExplanation("Giải thích cho điểm đề xuất.");
            itinerary.AddItem(optional);
            itinerary.AddItem(ItineraryItem.CreateRest(
                3,
                now.AddHours(2),
                now.AddHours(3),
                "Suggested rest stop"));
            context.Itineraries.Add(itinerary);
            await context.SaveChangesAsync();
            return itinerary.Id;
        });
    }

    private static async Task<(long HistoricalId, long CurrentId)> SeedVersionedAsync(
        TripMateApiFactory factory)
    {
        return await factory.WithDbContextAsync(async context =>
        {
            var now = DateTimeOffset.UtcNow;
            context.Users.Add(new User
            {
                Id = 1,
                Email = "versioned-itinerary-owner@example.com",
                FullName = "Versioned Itinerary Owner",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            var request = SchedulingRequest.Create(
                1,
                Guid.NewGuid(),
                new string('a', SchedulingRequest.RequestHashLength),
                now.AddDays(1),
                "Asia/Ho_Chi_Minh",
                16.0544m,
                108.2022m,
                16.0471m,
                108.2068m,
                null,
                true,
                480,
                TransportMode.Motorbike,
                10m,
                800_000m,
                "[]",
                RestPreference.None,
                now);
            var generationOwnerId = Guid.NewGuid();
            request.ClaimGeneration(generationOwnerId, now.AddMinutes(1), now);
            request.CompleteGeneration(generationOwnerId, now);
            var historical = Itinerary.CreateCspGenerated(
                request,
                "Historical version",
                now.AddDays(1),
                now.AddDays(1).AddHours(4));
            var historicalItem = ItineraryItem.CreateRest(
                1,
                now.AddDays(1),
                now.AddDays(1).AddHours(1),
                "Suggested rest stop");
            historicalItem.AttachFriendlyExplanation("Giải thích của phiên bản cũ.");
            historical.AddItem(historicalItem);
            var current = Itinerary.CreateCspGenerated(
                request,
                "Current version",
                now.AddDays(1),
                now.AddDays(1).AddHours(4),
                version: 2);
            var currentItem = ItineraryItem.CreateRest(
                1,
                now.AddDays(1),
                now.AddDays(1).AddHours(1),
                "Suggested rest stop");
            currentItem.AttachFriendlyExplanation("Giải thích của phiên bản hiện tại.");
            current.AddItem(currentItem);
            context.SchedulingRequests.Add(request);
            context.Itineraries.AddRange(historical, current);
            await context.SaveChangesAsync();
            return (historical.Id, current.Id);
        });
    }

    private sealed class CountingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public int SaveCalls { get; private set; }

        public void Reset() => SaveCalls = 0;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            SaveCalls++;
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CountingExplanationProvider : IItineraryExplanationProvider
    {
        public int CallCount { get; private set; }

        public Task<Result<ItineraryExplanationResult>> ExplainAsync(
            ItineraryExplanationInput input,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("The detail GET must not invoke the explanation provider.");
        }
    }
}