using System.Net;
using System.Text.Json;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.AuditLogs;

[Collection(nameof(TripMateApiFactory))]
public sealed class AuditLogListEndpointTests
{
    [Fact]
    public async Task GetAuditLogs_WhenFromDateAfterToDate_Returns400BadRequestWithValidationProblemDetails()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        var service = factory.Services.GetRequiredService<IJwtTokenService>();
        var token = service.GenerateAccessToken(new User
        {
            Id = 100,
            Email = "admin@example.com",
            FullName = "Admin",
            Role = UserRole.Administrator,
            Status = AccountStatus.Active,
        }).Token;
        using var client = factory.CreateJwtClient(token);

        var fromDate = "2026-09-15T00:00:00Z";
        var toDate = "2026-09-10T00:00:00Z";
        var response = await client.GetAsync($"/api/v1/admin/audit-logs?fromDateUtc={fromDate}&toDateUtc={toDate}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        
        body.RootElement.GetProperty("title").GetString().Should().Be("One or more validation errors occurred.");
        body.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        
        var errors = body.RootElement.GetProperty("errors");
        var hasDateRangeError = false;
        
        foreach (var error in errors.EnumerateObject())
        {
            foreach (var message in error.Value.EnumerateArray())
            {
                if (message.GetString() == "The submitted Event Date range is logically invalid.")
                {
                    hasDateRangeError = true;
                }
            }
        }
        
        hasDateRangeError.Should().BeTrue();
    }
}
