using System.Net;
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
public sealed class AuditLogDetailEndpointTests
{
    [Fact]
    public async Task Get_WithoutToken_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/audit-logs/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Select(header => header.Scheme).Should().Contain("Bearer");
    }

    [Theory]
    [InlineData(TestJwtKind.Malformed)]
    [InlineData(TestJwtKind.Expired)]
    [InlineData(TestJwtKind.InvalidSignature)]
    [InlineData(TestJwtKind.InvalidIssuer)]
    public async Task Get_WithInvalidToken_ReturnsUnauthorized(TestJwtKind kind)
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateJwtClient(TestJwtTokenFactory.Create(kind));

        var response = await client.GetAsync("/api/v1/admin/audit-logs/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task Get_WithNonAdministratorJwt_ReturnsForbidden(UserRole role)
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = CreateClient(factory, role);

        var response = await client.GetAsync("/api/v1/admin/audit-logs/1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("beforeData");
    }

    [Fact]
    public async Task Get_WithAdministratorAndMissingEntry_ReturnsNotFoundProblemDetails()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = CreateClient(factory, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/audit-logs/999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be("admin.audit_log_not_found");
        body.RootElement.GetProperty("title").GetString().Should().Be("System audit log entry not found.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_WithAdministrator_ReturnsMaskedDetailWithoutChangingStoredEntry(bool systemActor)
    {
        const string payload = """{"status":"Approved","reason":"Verified","nested":[{"password":"secret-fixture","paymentCredentials":{"cvv":"321"}}]}""";
        var time = new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        var id = await factory.WithDbContextAsync(async db =>
        {
            User? actor = null;
            if (!systemActor)
            {
                actor = new User
                {
                    Email = "audit-admin@example.com",
                    FullName = "Audit Administrator",
                    Role = UserRole.Administrator,
                    Status = AccountStatus.Active,
                    CreatedAtUtc = time,
                    UpdatedAtUtc = time,
                };
                db.Users.Add(actor);
                await db.SaveChangesAsync();
            }

            var log = new AuditLog(actor?.Id, "ApproveOperatorApplication", "OperatorProfile", 42, time,
                beforeData: payload, afterData: payload, ipAddress: "127.0.0.1");
            db.AuditLogs.Add(log);
            await db.SaveChangesAsync();
            return log.Id;
        });
        using var client = CreateClient(factory, UserRole.Administrator);

        var response = await client.GetAsync($"/api/v1/admin/audit-logs/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("id").GetInt64().Should().Be(id);
        root.TryGetProperty("data", out _).Should().BeFalse();
        root.GetProperty("actorFullName").GetString().Should().Be(systemActor ? "System" : "Audit Administrator");
        root.GetProperty("createdAtLocal").GetString().Should().Be("13/09/2026 17:00:00");
        foreach (var property in new[] { "beforeData", "afterData" })
        {
            var value = root.GetProperty(property).GetString()!;
            value.Should().NotContain("secret-fixture").And.NotContain("321");
            using var masked = JsonDocument.Parse(value);
            masked.RootElement.GetProperty("reason").GetString().Should().Be("Verified");
            masked.RootElement.GetProperty("nested")[0].GetProperty("password").GetString().Should().Be("[REDACTED]");
        }

        var persisted = await factory.WithDbContextAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(log => log.Id == id));
        persisted.BeforeData.Should().Be(payload);
        persisted.AfterData.Should().Be(payload);
    }

    [Theory]
    [InlineData("Success", "Business license verified.")]
    [InlineData("Failure", null)]
    public async Task Get_WithRecordedOutcome_ExposesResultAndRedactedReason(
        string outcomeString, string? reason)
    {
        var outcome = Enum.Parse<AuditOutcome>(outcomeString);
        var time = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        var id = await factory.WithDbContextAsync(async db =>
        {
            var log = AuditLog.CreateRecordedOutcome(
                null, "ApproveOperatorApplication", "OperatorProfile", 10, time,
                outcome, reason);
            db.AuditLogs.Add(log);
            await db.SaveChangesAsync();
            return log.Id;
        });
        using var client = CreateClient(factory, UserRole.Administrator);

        var response = await client.GetAsync($"/api/v1/admin/audit-logs/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("result").GetString().Should().Be(outcomeString);
        if (reason is null)
            body.RootElement.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        else
            body.RootElement.GetProperty("reason").GetString().Should().Be(reason);
    }

    [Fact]
    public async Task Get_WithLegacyEntry_ReturnsNullResultAndReason()
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        var id = await factory.WithDbContextAsync(async db =>
        {
            // Plain constructor — no Result/Reason recorded (historical entry)
            var log = new AuditLog(null, "LegacyAction", "OperatorProfile", 5, time);
            db.AuditLogs.Add(log);
            await db.SaveChangesAsync();
            return log.Id;
        });
        using var client = CreateClient(factory, UserRole.Administrator);

        var response = await client.GetAsync($"/api/v1/admin/audit-logs/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static HttpClient CreateClient(TripMateApiFactory factory, UserRole role)
    {
        var service = factory.Services.GetRequiredService<IJwtTokenService>();
        var token = service.GenerateAccessToken(new User
        {
            Id = 100,
            Email = "audit-reader@example.com",
            FullName = "Audit Reader",
            Role = role,
            Status = AccountStatus.Active,
        }).Token;
        return factory.CreateJwtClient(token);
    }
}