using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.PointsOfInterest.Management;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public sealed class ManagedPoiListEndpointTests
{
    private const string Route = "/api/v1/admin/pois";
    private static readonly DateTimeOffset Time = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.Staff)]
    public async Task Get_WithActiveManager_ReturnsDirectLosslessDtoAndCompleteMetadata(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        var actorId = await SeedAsync(factory, role);
        using var client = factory.CreateAuthenticatedClient(actorId, role);
        var response = await client.GetAsync(Route);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStore(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.TryGetProperty("data", out _).Should().BeFalse();
        root.GetProperty("page").GetInt32().Should().Be(ListManagedPoisQuery.DefaultPage);
        root.GetProperty("pageSize").GetInt32().Should().Be(ListManagedPoisQuery.DefaultPageSize);
        root.GetProperty("totalCount").GetInt32().Should().Be(2);
        var categories = root.GetProperty("metadata").GetProperty("categories").EnumerateArray().ToArray();
        categories.Select(c => c.GetProperty("name").GetString()).Should().Equal("Attraction", "Empty");
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        items.Select(i => i.GetProperty("id").GetString()).Should().Equal("9007199254740993", "9223372036854775807");
        items[0].GetProperty("address").ValueKind.Should().Be(JsonValueKind.Null);
        items.Select(i => i.GetProperty("status").GetString()).Should().Equal("Active", "Inactive");
        var hours = items[0].GetProperty("openingHours").EnumerateArray().ToArray();
        hours[0].GetProperty("dayOfWeek").GetByte().Should().Be(0);
        hours[0].GetProperty("openTime").GetString().Should().Be("08:00:00");
        hours[0].GetProperty("closeTime").GetString().Should().Be("17:00:00");
        hours[0].GetProperty("isClosed").GetBoolean().Should().BeFalse();
        items[1].GetProperty("openingHours").GetArrayLength().Should().Be(0);
        var counts = await factory.WithDbContextAsync(async db => new[]
        {
            await db.PointsOfInterest.CountAsync(), await db.AuditLogs.CountAsync(),
        });
        counts.Should().Equal(2, 0);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=2147483648")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("page=abc")]
    [InlineData("pageSize=101")]
    [InlineData("status=active")]
    [InlineData("status=")]
    [InlineData("status=%20%20")]
    [InlineData("categoryId=")]
    [InlineData("categoryId=2147483648")]
    [InlineData("categoryId=0")]
    [InlineData("page=1&page=2")]
    [InlineData("pageSize=1&pageSize=2")]
    [InlineData("search=a&search=b")]
    [InlineData("status=Active&status=Inactive")]
    [InlineData("categoryId=1&categoryId=2")]
    public async Task Get_WithInvalidRecognizedQuery_ReturnsValidationProblemAndNoStore(string query)
    {
        await using var factory = new TripMateApiFactory();
        var actorId = await SeedAsync(factory, UserRole.Staff);
        using var client = factory.CreateAuthenticatedClient(actorId, UserRole.Staff);
        var response = await client.GetAsync($"{Route}?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNoStore(response);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Get_UnknownCategory_Returns400ButKnownEmptyAndBeyondLastReturnRealEmpty200()
    {
        await using var factory = new TripMateApiFactory();
        var actorId = await SeedAsync(factory, UserRole.Administrator);
        using var client = factory.CreateAuthenticatedClient(actorId, UserRole.Administrator);
        var unknown = await client.GetAsync($"{Route}?categoryId=2147483647");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNoStore(unknown);
        using var problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be(ManagedPoiErrors.CategoryNotFound);
        var emptyId = await factory.WithDbContextAsync(db => Task.FromResult(db.PoiCategories.Single(c => c.Name == "Empty").Id));
        foreach (var query in new[] { $"categoryId={emptyId}", "search=does-not-exist", "page=8&pageSize=1" })
        {
            var response = await client.GetAsync($"{Route}?{query}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertNoStore(response);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
            body.RootElement.GetProperty("metadata").GetProperty("categories").GetArrayLength().Should().Be(2);
        }
    }

    [Fact]
    public async Task Get_WithoutAuthentication_RejectsBeforeInvalidBindingAndPreventsCaching()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var response = await client.GetAsync($"{Route}?page=invalid");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNoStore(response);
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task Get_WithWrongRole_RejectsBeforeBindingWithSafe403(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, role);
        var response = await client.GetAsync($"{Route}?page=invalid");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        AssertNoStore(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(ManagedPoiErrors.AccessRequired);
    }

    [Fact]
    public async Task Get_PersistenceFailure_UsesExistingSafe500AndNoStore()
    {
        await using var factory = new TripMateApiFactory(configureTestServices: services =>
        {
            services.RemoveAll<IRequestHandler<ListManagedPoisQuery, Result<ManagedPoiListDto>>>();
            services.AddTransient<IRequestHandler<ListManagedPoisQuery, Result<ManagedPoiListDto>>, ThrowingListHandler>();
        });
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Staff);
        var response = await client.GetAsync(Route);
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        AssertNoStore(response);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain("private-database-marker");
        using var body = JsonDocument.Parse(json);
        body.RootElement.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
    }

    [Fact]
    public async Task SharedGet_DoesNotBroadenAdministratorPostOrCataloguePermissions()
    {
        await using var factory = new TripMateApiFactory();
        var actorId = await SeedAsync(factory, UserRole.Administrator);
        using var admin = factory.CreateAuthenticatedClient(actorId, UserRole.Administrator);
        (await admin.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync(Route, new { name = "No create permission" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.GetAsync($"{Route}/catalogue")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    internal static void AssertNoStore(HttpResponseMessage response) => (response.Headers.CacheControl?.NoStore).Should().BeTrue();

    private static Task<long> SeedAsync(TripMateApiFactory factory, UserRole role) => factory.WithDbContextAsync(async db =>
    {
        var actor = new User { Email = $"{Guid.NewGuid():N}@example.com", FullName = "Management actor", Role = role, Status = AccountStatus.Active, CreatedAtUtc = Time, UpdatedAtUtc = Time };
        var category = PoiCategory.Create("Attraction", null);
        db.Users.Add(actor);
        db.PoiCategories.AddRange(category, PoiCategory.Create("Empty", null));
        await db.SaveChangesAsync();
        var active = PointOfInterest.Create(category, "A POI", 16m, 108m, actor.Id, Time);
        active.Id = 9007199254740993;
        active.AddOpeningHour(PoiOpeningHour.Create(0, new TimeOnly(8, 0), new TimeOnly(17, 0), false));
        var inactive = PointOfInterest.Create(category, "Z POI", 16m, 108m, actor.Id, Time, "Inactive address");
        inactive.Id = long.MaxValue;
        db.PointsOfInterest.AddRange(active, inactive);
        db.Entry(inactive).Property(p => p.Status).CurrentValue = PointOfInterestStatus.Inactive;
        await db.SaveChangesAsync();
        return actor.Id;
    });

    private sealed class ThrowingListHandler : IRequestHandler<ListManagedPoisQuery, Result<ManagedPoiListDto>>
    {
        public Task<Result<ManagedPoiListDto>> Handle(ListManagedPoisQuery request, CancellationToken cancellationToken) => throw new InvalidOperationException("private-database-marker");
    }
}