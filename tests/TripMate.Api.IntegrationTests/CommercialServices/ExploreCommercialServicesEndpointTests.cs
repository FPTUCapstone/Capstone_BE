using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.CommercialServices.Explore;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.CommercialServices;

[Collection(nameof(TripMateApiFactory))]
public sealed class ExploreCommercialServicesEndpointTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Get_SqlServerSearchAndPaging_UsesVisibilityRulesAndStableOrder()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using (var context = database.CreateDbContext())
        {
            var provider = ServiceProvider.Create(
                "Da Nang Ride",
                CommercialService.CategoryVehicle,
                ServiceProvider.StatusActive);
            var alpha = CommercialService.Create(
                provider,
                CommercialService.CategoryVehicle,
                "Alpha scooter",
                180000m,
                CommercialService.PriceUnitPerDay,
                CommercialService.AvailabilityAvailable,
                "VND",
                true,
                null,
                null,
                """{"transmission":"Automatic"}""",
                DateTimeOffset.UtcNow);
            var beta = CommercialService.Create(
                provider,
                CommercialService.CategoryVehicle,
                "Beta scooter",
                190000m,
                CommercialService.PriceUnitPerDay,
                CommercialService.AvailabilityAvailable,
                "VND",
                true,
                null,
                null,
                """{"internal":"must-not-leak"}""",
                DateTimeOffset.UtcNow);
            var unavailable = CommercialService.Create(
                provider,
                CommercialService.CategoryVehicle,
                "Hidden scooter",
                200000m,
                CommercialService.PriceUnitPerDay,
                CommercialService.AvailabilityUnavailable,
                "VND",
                true,
                null,
                null,
                null,
                DateTimeOffset.UtcNow);

            context.CommercialServices.AddRange(alpha, beta, unavailable);
            await context.SaveChangesAsync();
        }

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/v1/commercial-services?category=Vehicle&search=SCOOTER&page=1&pageSize=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedCommercialServiceResponseDto>();
        body.Should().NotBeNull();
        body!.TotalCount.Should().Be(2);
        body.Items.Should().ContainSingle();
        body.Items[0].Name.Should().Be("Alpha scooter");
        body.Items[0].Attributes.Should().ContainKey("transmission");
        body.Items[0].Attributes.Should().NotContainKey("internal");
    }

    [Fact]
    public async Task Get_Anonymous_ReturnsOnlyVisibleServices()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/commercial-services?category=Vehicle");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedCommercialServiceResponseDto>();
        body.Should().NotBeNull();
        body!.Items.Should().ContainSingle(item => item.Id == seed.VisibleServiceId);
        body.Items.Single().ProviderName.Should().Be("Da Nang Ride");
    }

    [Fact]
    public async Task Get_AuthenticatedTraveler_ReceivesTheSamePublicCatalog()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var anonymousClient = factory.CreateClient();
        using var travelerClient = factory.CreateAuthenticatedClient(91, UserRole.Traveler);

        var anonymousResponse = await anonymousClient.GetAsync(
            "/api/v1/commercial-services?category=Vehicle");
        var travelerResponse = await travelerClient.GetAsync(
            "/api/v1/commercial-services?category=Vehicle");

        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        travelerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var anonymousPayload = await anonymousResponse.Content.ReadAsStringAsync();
        var travelerPayload = await travelerResponse.Content.ReadAsStringAsync();
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var anonymousBody = JsonSerializer.Deserialize<PagedCommercialServiceResponseDto>(
            anonymousPayload,
            serializerOptions);
        var travelerBody = JsonSerializer.Deserialize<PagedCommercialServiceResponseDto>(
            travelerPayload,
            serializerOptions);
        anonymousBody.Should().NotBeNull();
        travelerBody.Should().NotBeNull();
        travelerPayload.Should().Be(anonymousPayload);
        travelerBody.Items.Should().ContainSingle(item => item.Id == seed.VisibleServiceId);
    }

    [Fact]
    public async Task Get_DetailForUnavailableService_ReturnsNeutralNotFound()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/commercial-services/{seed.UnavailableServiceId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_Detail_ExposesOnlyApprovedPublicProviderData()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/commercial-services/{seed.VisibleServiceId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("providerName").GetString().Should().Be("Da Nang Ride");
        root.GetProperty("currencyCode").GetString().Should().Be("VND");
        root.TryGetProperty("apiEndpoint", out _).Should().BeFalse();
        root.TryGetProperty("commissionRate", out _).Should().BeFalse();
        root.TryGetProperty("attributes", out var attributes).Should().BeTrue();
        attributes.TryGetProperty("internal", out _).Should().BeFalse();
    }

    private static Task<SeedResult> SeedAsync(TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async context =>
        {
            var provider = ServiceProvider.Create(
                "Da Nang Ride",
                CommercialService.CategoryVehicle,
                ServiceProvider.StatusActive);
            var visible = CommercialService.Create(
                provider,
                CommercialService.CategoryVehicle,
                "Honda Wave 110cc",
                180000m,
                CommercialService.PriceUnitPerDay,
                CommercialService.AvailabilityAvailable,
                "VND",
                true,
                500000m,
                null,
                """{"transmission":"Automatic","internal":"hidden"}""",
                DateTimeOffset.UtcNow);
            var unavailable = CommercialService.Create(
                provider,
                CommercialService.CategoryVehicle,
                "Unavailable vehicle",
                120000m,
                CommercialService.PriceUnitPerDay,
                CommercialService.AvailabilityUnavailable,
                "VND",
                true,
                null,
                null,
                null,
                DateTimeOffset.UtcNow);

            context.CommercialServices.AddRange(visible, unavailable);
            await context.SaveChangesAsync();
            return new SeedResult(visible.Id, unavailable.Id);
        });

    private sealed record SeedResult(long VisibleServiceId, long UnavailableServiceId);
}