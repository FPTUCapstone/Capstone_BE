using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.AuditLogs;

[Collection(nameof(TripMateApiFactory))]
public sealed class AuditConfigEndpointTests
{
    [Fact]
    public async Task Get_WithoutToken_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/system-configs/algorithm-parameters");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Select(header => header.Scheme).Should().Contain("Bearer");
    }

    [Fact]
    public async Task Put_WithInvalidToken_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateJwtClient(TestJwtTokenFactory.Create(TestJwtKind.Expired));

        var response = await client.PutAsync(
            "/api/v1/admin/system-configs/algorithm-parameters",
            JsonBody(new { bufferTimeMinutes = 20 }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_WithNonAdministrator_ReturnsForbiddenProblemDetails()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = CreateClient(factory, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/admin/system-configs/algorithm-parameters");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("bufferTimeMinutes");
    }

    [Fact]
    public async Task Get_WithAdministrator_ReturnsParameterContract()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = CreateClient(factory, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/system-configs/algorithm-parameters");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("bufferTimeMinutes").GetInt32().Should().Be(15);
        root.GetProperty("defaultTravelSpeedKmh").GetDouble().Should().Be(30);
        root.GetProperty("reroutingSearchRadiusKm").GetDouble().Should().Be(5);
        root.GetProperty("weatherAlertThresholdSeverity").GetString().Should().Be("Severe");
        root.TryGetProperty("data", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Put_WithAdministrator_PersistsRowsAndWritesSuccessAuditEntry()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = CreateClient(factory, UserRole.Administrator);

        var response = await client.PutAsync(
            "/api/v1/admin/system-configs/algorithm-parameters",
            JsonBody(new
            {
                bufferTimeMinutes = 25,
                defaultTravelSpeedKmh = 40,
                reroutingSearchRadiusKm = 12,
                weatherAlertThresholdSeverity = "moderate",
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("bufferTimeMinutes").GetInt32().Should().Be(25);
        body.RootElement.GetProperty("weatherAlertThresholdSeverity").GetString().Should().Be("Moderate");

        var rows = await factory.WithDbContextAsync(async db =>
            await db.SystemConfigs.AsNoTracking().ToListAsync());
        rows.Should().HaveCount(4);
        rows.Single(row => row.ConfigKey == "CSP.BufferTimeMinutes").ConfigValue.Should().Be("25");
        rows.Single(row => row.ConfigKey == "CSP.BufferTimeMinutes").UpdatedBy.Should().Be(100);
        rows.Single(row => row.ConfigKey == "Weather.AlertThresholdSeverity").ConfigValue.Should().Be("Moderate");

        var audit = await factory.WithDbContextAsync(db =>
            db.AuditLogs.AsNoTracking().SingleAsync());
        audit.ActionType.Should().Be("UpdateAlgorithmParameters");
        audit.AffectedEntity.Should().Be("SystemConfig");
        audit.ActorUserId.Should().Be(100);
        audit.Result.Should().Be(AuditOutcome.Success);
        audit.AfterData.Should().Contain("CSP.BufferTimeMinutes");
    }

    [Fact]
    public async Task Put_WithValueOutOfRange_Returns422AndChangesNothing()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = CreateClient(factory, UserRole.Administrator);
        await factory.WithDbContextAsync<bool>(async db =>
        {
            db.SystemConfigs.Add(new SystemConfig(
                "CSP.BufferTimeMinutes", "15", description: null, updatedBy: 1,
                DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            return true;
        });

        var response = await client.PutAsync(
            "/api/v1/admin/system-configs/algorithm-parameters",
            JsonBody(new
            {
                bufferTimeMinutes = 61,
                defaultTravelSpeedKmh = 35,
                reroutingSearchRadiusKm = 10,
                weatherAlertThresholdSeverity = "Severe",
            }));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("out of allowed range");

        var rows = await factory.WithDbContextAsync(async db =>
            await db.SystemConfigs.AsNoTracking().ToListAsync());
        rows.Should().HaveCount(1);
        rows.Single().ConfigValue.Should().Be("15");

        var auditCount = await factory.WithDbContextAsync(db => db.AuditLogs.CountAsync());
        auditCount.Should().Be(0);
    }

    private static HttpClient CreateClient(TripMateApiFactory factory, UserRole role)
    {
        var service = factory.Services.GetRequiredService<IJwtTokenService>();
        var token = service.GenerateAccessToken(new User
        {
            Id = 100,
            Email = "config-admin@example.com",
            FullName = "Config Administrator",
            Role = role,
            Status = AccountStatus.Active,
        }).Token;
        return factory.CreateJwtClient(token);
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
}