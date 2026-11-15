using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Routes;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateRouteSqlServerTests
{
    private const string Endpoint = "/api/v1/admin/routes";
    private static readonly DateTimeOffset SeedTime = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Post_PersistsActiveRouteAndAudit_AndServerListReadsIt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var client = CreateJwtClient(factory, seed.Staff);

        var response = await PostAsync(client, seed);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var routeId = body.RootElement.GetProperty("id").GetInt64();
        body.RootElement.GetProperty("status").GetString().Should().Be("Active");
        body.RootElement.GetProperty("messageCode").GetString().Should().Be("MSG139");
        body.RootElement.GetProperty("message").GetString().Should().Be("Route created successfully.");
        body.RootElement.GetProperty("routeNote").GetString().Should().Be("Scenic road");

        var stored = await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM catalog.RouteSegments r
            INNER JOIN catalog.POIs origin ON origin.poi_id = r.from_poi_id
            INNER JOIN catalog.POIs destination ON destination.poi_id = r.to_poi_id
            WHERE r.segment_id = {routeId} AND r.status = 'Active'
                AND origin.status = 'Active' AND destination.status = 'Active'
                AND r.from_poi_id <> r.to_poi_id AND r.distance_km = 12.50
                AND r.est_duration_minutes = 25 AND r.transport_mode = 'Car';
            """);
        stored.Should().Be(1, "PC-01 must hold in the committed SQL row");

        await using var context = database.CreateDbContext();
        var audit = await context.AuditLogs.AsNoTracking().SingleAsync(item => item.AffectedEntityId == routeId);
        audit.ActorUserId.Should().Be(seed.Staff.Id);
        audit.ActionType.Should().Be("ROUTE_CREATE");
        audit.AfterData.Should().Contain(routeId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var list = await client.GetAsync(Endpoint);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        listJson.RootElement.GetProperty("items").EnumerateArray()
            .Should().Contain(item => item.GetProperty("id").GetInt64() == routeId);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task Post_NonStaffIsForbidden_AndPersistsNothing(UserRole role)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, role);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var client = CreateJwtClient(factory, seed.Staff);

        var response = await PostAsync(client, seed);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("MSG126");
        await AssertNoRouteOrAuditAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Post_AccountLockedAfterTokenIssued_IsForbidden()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var client = CreateJwtClient(factory, seed.Staff);
        await database.ExecuteNonQueryAsync($"UPDATE dbo.Users SET status = 'Locked' WHERE user_id = {seed.Staff.Id};");

        var response = await PostAsync(client, seed);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        await AssertNoRouteOrAuditAsync(database);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("missing-origin", "MSG01")]
    [InlineData("nonexistent-origin", "MSG141")]
    [InlineData("nonexistent-destination", "MSG141")]
    [InlineData("same-endpoints", "MSG141")]
    [InlineData("inactive-origin", "MSG141")]
    [InlineData("inactive-destination", "MSG141")]
    [InlineData("zero-distance", "MSG143")]
    [InlineData("negative-duration", "MSG143")]
    public async Task Post_InvalidRequest_ReturnsDomainMessageAndPersistsNothing(string invalidCase, string message)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var client = CreateJwtClient(factory, seed.Staff);
        var input = Input(seed);
        switch (invalidCase)
        {
            case "missing-origin": input.Remove("fromPoiId"); break;
            case "nonexistent-origin": input["fromPoiId"] = long.MaxValue; break;
            case "nonexistent-destination": input["toPoiId"] = long.MaxValue; break;
            case "same-endpoints": input["toPoiId"] = seed.FromId; break;
            case "zero-distance": input["distanceKm"] = 0; break;
            case "negative-duration": input["estimatedDurationMinutes"] = -1; break;
            case "inactive-origin":
                await database.ExecuteNonQueryAsync($"UPDATE catalog.POIs SET status = 'Inactive' WHERE poi_id = {seed.FromId};");
                break;
            case "inactive-destination":
                await database.ExecuteNonQueryAsync($"UPDATE catalog.POIs SET status = 'Inactive' WHERE poi_id = {seed.ToId};");
                break;
        }

        var response = await PostAsync(client, seed, input: input);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain(message);
        await AssertNoRouteOrAuditAsync(database);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Post_WhenAuditOrRouteSaveFails_ReturnsMsg127AndRollsBackBoth(bool rejectAudit)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var client = CreateJwtClient(factory, seed.Staff);
        await database.ExecuteNonQueryAsync(rejectAudit
            ? "ALTER TABLE dbo.AuditLogs ADD CONSTRAINT CK_TM101_RejectAudit CHECK (action_type <> 'ROUTE_CREATE');"
            : "ALTER TABLE catalog.RouteSegments ADD CONSTRAINT CK_TM101_RejectRoute CHECK (distance_km <> 12.50);");

        var response = await PostAsync(client, seed);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("MSG127").And.NotContain("CK_TM101").And.NotContain("SqlException");
        await AssertNoRouteOrAuditAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Post_RepeatedOperationIsRejected_WithoutSecondRouteOrAudit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var client = CreateJwtClient(factory, seed.Staff);
        var operation = Guid.NewGuid();
        (await PostAsync(client, seed, operation)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await PostAsync(client, seed, operation)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.RouteSegments;")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AuditLogs WHERE action_type = 'ROUTE_CREATE';")).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Post_ConcurrentSameOperationCommitsExactlyOneRouteAndAudit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var firstClient = CreateJwtClient(factory, seed.Staff);
        using var secondClient = CreateJwtClient(factory, seed.Staff);
        var operation = Guid.NewGuid();
        var responses = await Task.WhenAll(PostAsync(firstClient, seed, operation), PostAsync(secondClient, seed, operation));
        responses.Count(response => response.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.RouteSegments;")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AuditLogs WHERE action_type = 'ROUTE_CREATE';")).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Options_OnlyActivePoisAreReturned()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await database.ExecuteNonQueryAsync($"UPDATE catalog.POIs SET status = 'Inactive' WHERE poi_id = {seed.ToId};");
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var client = CreateJwtClient(factory, seed.Staff);
        var response = await client.GetAsync(Endpoint + "/options");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("pois").EnumerateArray().Select(item => item.GetProperty("id").GetInt64())
            .Should().Contain(seed.FromId).And.NotContain(seed.ToId);
        json.RootElement.GetProperty("transportModes").EnumerateArray().Select(item => item.GetString())
            .Should().BeEquivalentTo("Walking", "Motorbike", "Car", "PublicTransit");
    }

    internal static async Task<Seed> SeedAsync(SqlServerTestDatabase database, UserRole role = UserRole.Staff)
    {
        await using var context = database.CreateDbContext();
        var staff = new User
        {
            Email = "tm101-sql@example.com",
            FullName = "Route Staff",
            Role = role,
            Status = AccountStatus.Active,
            CreatedAtUtc = SeedTime,
            UpdatedAtUtc = SeedTime,
        };
        var category = PoiCategory.Create("TM101 SQL category", null);
        context.Users.Add(staff);
        context.PoiCategories.Add(category);
        await context.SaveChangesAsync();
        var from = PointOfInterest.Create(category, "Route origin", 16.06m, 108.20m, staff.Id, SeedTime);
        var to = PointOfInterest.Create(category, "Route destination", 16.10m, 108.25m, staff.Id, SeedTime);
        context.PointsOfInterest.AddRange(from, to);
        await context.SaveChangesAsync();
        return new Seed(staff, from.Id, to.Id);
    }

    private static HttpClient CreateJwtClient(TripMateApiFactory factory, User user)
    {
        var jwt = factory.Services.GetRequiredService<IJwtTokenService>();
        return factory.CreateJwtClient(jwt.GenerateAccessToken(user).Token);
    }

    private static Dictionary<string, object?> Input(Seed seed) => new()
    {
        ["fromPoiId"] = seed.FromId,
        ["toPoiId"] = seed.ToId,
        ["distanceKm"] = 12.50m,
        ["estimatedDurationMinutes"] = 25,
        ["transportMode"] = "Car",
        ["routeNote"] = "Scenic road",
    };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Seed seed,
        Guid? operation = null, Dictionary<string, object?>? input = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(input ?? Input(seed)),
        };
        request.Headers.Add("Idempotency-Key", (operation ?? Guid.NewGuid()).ToString());
        return await client.SendAsync(request);
    }

    private static async Task AssertNoRouteOrAuditAsync(SqlServerTestDatabase database)
    {
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.RouteSegments;")).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AuditLogs WHERE action_type = 'ROUTE_CREATE';")).Should().Be(0);
    }

    internal sealed record Seed(User Staff, long FromId, long ToId);
}