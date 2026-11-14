using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Routes;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Routes;

public class CreateRouteEndpointTests
{
    private const string Path = "/api/v1/admin/routes";

    [Fact]
    public async Task StaffCreate_IsServerPersistedAndListed_WithAuditAndRepeatProtection()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await Seed(factory);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, UserRole.Staff);
        var key = Guid.NewGuid();
        using var response = await Post(client, Body(seed), key);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var route = await response.Content.ReadFromJsonAsync<RouteDto>();
        route!.MessageCode.Should().Be(RouteErrors.Msg139);
        route.Message.Should().Be(RouteErrors.Created);
        route.Status.Should().Be(nameof(RouteStatus.Active));
        route.RouteNote.Should().BeNull();
        var list = await client.GetFromJsonAsync<RouteListDto>(Path);
        list!.Items.Should().ContainSingle(item => item.Id == route.Id);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RouteSegments.SingleAsync()).Id.Should().Be(route.Id);
            var audit = await db.AuditLogs.SingleAsync(row => row.ActionType == AuditActionTypes.RouteCreate);
            audit.AffectedEntityId.Should().Be(route.Id);
            return true;
        });
        using var repeat = await Post(client, Body(seed), key);
        repeat.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await Error(repeat, RouteErrors.DuplicateSubmission);
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task OtherRoles_CannotCreateOrOpenOptions(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        var seed = await Seed(factory, role);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, role);
        using var response = await Post(client, Body(seed), Guid.NewGuid());
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await Error(response, RouteErrors.Msg126);
        using var options = await client.GetAsync(Path + "/options");
        options.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnauthenticatedRejected()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        using var response = await Post(client, new { }, Guid.NewGuid());
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InactiveAccount_WithStaffTokenIsRejected()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await Seed(factory, status: AccountStatus.Inactive);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, UserRole.Staff);
        using var response = await Post(client, Body(seed), Guid.NewGuid());
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await Error(response, RouteErrors.Msg126);
    }

    [Theory]
    [InlineData("{}", "MSG01")]
    [InlineData("{\"fromPoiId\":1,\"toPoiId\":1,\"distanceKm\":1,\"estimatedDurationMinutes\":1,\"transportMode\":\"Walking\"}", "MSG141")]
    [InlineData("{\"fromPoiId\":1,\"toPoiId\":2,\"distanceKm\":0,\"estimatedDurationMinutes\":1,\"transportMode\":\"Walking\"}", "MSG143")]
    [InlineData("{\"fromPoiId\":1,\"toPoiId\":2,\"distanceKm\":1,\"estimatedDurationMinutes\":0,\"transportMode\":\"Walking\"}", "MSG143")]
    [InlineData("{\"fromPoiId\":1,\"toPoiId\":2,\"distanceKm\":1,\"estimatedDurationMinutes\":1,\"transportMode\":\"Unknown\"}", "Routes.InvalidInput")]
    public async Task ValidationProblemsCarryExactMessageCodeAndWriteNothing(string json, string code)
    {
        await using var factory = new TripMateApiFactory();
        var seed = await Seed(factory);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, UserRole.Staff);
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Error(response, code);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RouteSegments.CountAsync()).Should().Be(0);
            (await db.AuditLogs.CountAsync()).Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task OptionsOnlyReturnActivePoisAndCanonicalModes()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await Seed(factory);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, UserRole.Staff);
        var options = await client.GetFromJsonAsync<RouteOptionsDto>(Path + "/options");
        options!.Pois.Should().HaveCount(2);
        options.TransportModes.Should().Equal("Walking", "Motorbike", "Car", "PublicTransit");
    }

    [Fact]
    public async Task OpenApi_DescribesRequiredFieldsModeEnumNoteLimitAndFailureContracts()
    {
        await using var factory = new TripMateApiFactory();
        var provider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var serialized = await provider.GetSwagger("v1").SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;
        var post = root.GetProperty("paths").GetProperty(Path).GetProperty("post");
        var operationKey = post.GetProperty("parameters").EnumerateArray().Single(parameter =>
            parameter.GetProperty("name").GetString() == "Idempotency-Key");
        operationKey.GetProperty("in").GetString().Should().Be("header");
        operationKey.TryGetProperty("required", out var requiredKey).Should().BeTrue();
        requiredKey.GetBoolean().Should().BeTrue();
        var responses = post.GetProperty("responses");
        foreach (var code in new[] { "201", "400", "401", "403", "409", "503" })
            responses.TryGetProperty(code, out _).Should().BeTrue();
        var schema = root.GetProperty("components").GetProperty("schemas").GetProperty(nameof(CreateRouteCommand));
        schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should()
            .BeEquivalentTo("fromPoiId", "toPoiId", "distanceKm", "estimatedDurationMinutes", "transportMode");
        var properties = schema.GetProperty("properties");
        properties.GetProperty("transportMode").GetProperty("enum").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("Walking", "Motorbike", "Car", "PublicTransit");
        properties.GetProperty("routeNote").GetProperty("maxLength").GetInt32().Should().Be(2000);
        properties.TryGetProperty("idempotencyKey", out _).Should().BeFalse();
    }

    private static async Task Error(HttpResponseMessage response, string code)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errorCode").GetString().Should().Be(code);
    }

    private static object Body(SeedResult seed) => new
    {
        fromPoiId = seed.FromId,
        toPoiId = seed.ToId,
        distanceKm = 2.35m,
        estimatedDurationMinutes = 12,
        transportMode = "Walking",
        routeNote = " "
    };

    private static Task<HttpResponseMessage> Post(HttpClient client, object body, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return client.SendAsync(request);
    }

    private static Task<SeedResult> Seed(TripMateApiFactory factory, UserRole role = UserRole.Staff,
        AccountStatus status = AccountStatus.Active) => factory.WithDbContextAsync(async db =>
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User { Role = role, Status = status, FullName = "Route Staff", Email = $"{Guid.NewGuid():N}@example.com" };
        var category = PoiCategory.Create("Route attraction", null);
        db.Users.Add(user); db.PoiCategories.Add(category); await db.SaveChangesAsync();
        var from = PointOfInterest.Create(category, "Origin", 16m, 108m, user.Id, now);
        var to = PointOfInterest.Create(category, "Destination", 16.1m, 108.1m, user.Id, now);
        var inactive = PointOfInterest.Create(category, "Inactive", 16.2m, 108.2m, user.Id, now);
        inactive.TryDeactivate(now);
        db.PointsOfInterest.AddRange(from, to, inactive); await db.SaveChangesAsync();
        return new SeedResult(user.Id, from.Id, to.Id);
    });

    private sealed record SeedResult(long UserId, long FromId, long ToId);
}