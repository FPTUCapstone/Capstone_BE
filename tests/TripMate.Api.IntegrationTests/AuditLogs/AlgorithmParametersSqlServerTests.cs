using System.Net;
using System.Net.Http.Json;
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
public sealed class AlgorithmParametersSqlServerTests
{
    private const string Endpoint = "/api/v1/admin/system-configs/algorithm-parameters";
    private static readonly string[] ManagedKeys =
    [
        "CSP.BufferTimeMinutes",
        "CSP.DefaultTravelSpeedKmh",
        "Rerouting.SearchRadiusKm",
        "Weather.AlertThresholdSeverity",
    ];

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Put_PersistsFourParametersAndSuccessAudit_LeavesOtherConfigurationsUntouched()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var administrator = await SeedAdministratorAsync(database);
        var before = await ReadConfigurationsAsync(database);
        before.Should().HaveCount(8);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var client = CreateClient(factory, administrator);

        var response = await client.PutAsJsonAsync(Endpoint, Payload());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ReadConfigurationsAsync(database);
        after.Should().HaveCount(8);
        after.Where(row => !ManagedKeys.Contains(row.ConfigKey)).Should().BeEquivalentTo(
            before.Where(row => !ManagedKeys.Contains(row.ConfigKey)));
        var values = after.Where(row => ManagedKeys.Contains(row.ConfigKey))
            .ToDictionary(row => row.ConfigKey, row => row.ConfigValue);
        values.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [ManagedKeys[0]] = "25",
            [ManagedKeys[1]] = "42.5",
            [ManagedKeys[2]] = "12.5",
            [ManagedKeys[3]] = "Moderate",
        });
        after.Where(row => ManagedKeys.Contains(row.ConfigKey))
            .Should().OnlyContain(row => row.UpdatedBy == administrator.Id);

        await using var verification = database.CreateDbContext();
        var audit = await verification.AuditLogs.AsNoTracking().SingleAsync();
        audit.ActionType.Should().Be("UpdateAlgorithmParameters");
        audit.AffectedEntity.Should().Be("SystemConfig");
        audit.ActorUserId.Should().Be(administrator.Id);
        audit.Result.Should().Be(AuditOutcome.Success);
        JsonSerializer.Deserialize<Dictionary<string, string>>(audit.BeforeData!)
            .Should().BeEquivalentTo(before.Where(row => ManagedKeys.Contains(row.ConfigKey))
                .ToDictionary(row => row.ConfigKey, row => row.ConfigValue));
        JsonSerializer.Deserialize<Dictionary<string, string>>(audit.AfterData!)
            .Should().BeEquivalentTo(values);

        using var readBack = await client.GetAsync(Endpoint);
        readBack.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await readBack.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("defaultTravelSpeedKmh").GetDouble().Should().Be(42.5);
        body.RootElement.GetProperty("reroutingSearchRadiusKm").GetDouble().Should().Be(12.5);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Put_WhenAuditInsertIsRejected_RollsBackAllConfigurations()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var administrator = await SeedAdministratorAsync(database);
        var before = await ReadConfigurationsAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE dbo.AuditLogs
            ADD CONSTRAINT CK_AuditLogs_RejectAlgorithmUpdateForTest
            CHECK (action_type <> 'UpdateAlgorithmParameters' OR result <> 'Success');
            """);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var client = CreateClient(factory, administrator);

        var response = await client.PutAsJsonAsync(Endpoint, Payload());

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("CK_AuditLogs_RejectAlgorithmUpdateForTest");
        body.Should().NotContain("SqlException");
        (await ReadConfigurationsAsync(database)).Should().BeEquivalentTo(before,
            "configuration values and update metadata must roll back with the rejected audit insert");
        await using var verification = database.CreateDbContext();
        var audit = await verification.AuditLogs.AsNoTracking().SingleAsync();
        audit.ActionType.Should().Be("UpdateAlgorithmParameters");
        audit.AffectedEntity.Should().Be("SystemConfig");
        audit.ActorUserId.Should().Be(administrator.Id);
        audit.Result.Should().Be(AuditOutcome.Failure);
        audit.BeforeData.Should().BeNull();
        audit.Reason.Should().BeNull();
        using var metadata = JsonDocument.Parse(audit.AfterData!);
        metadata.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().Equal("auditMetadata");
        var failureContext = metadata.RootElement.GetProperty("auditMetadata");
        failureContext.EnumerateObject().Select(property => property.Name)
            .Should().Equal("errorCode");
        failureContext.GetProperty("errorCode").GetString().Should().Be("audit.database_write_failed");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Put_WithInvalidRange_Returns422WithoutChangingAnyConfigurationOrAudit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var administrator = await SeedAdministratorAsync(database);
        var before = await ReadConfigurationsAsync(database);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        using var client = CreateClient(factory, administrator);

        var response = await client.PutAsJsonAsync(Endpoint, Payload(bufferTimeMinutes: 61));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadConfigurationsAsync(database)).Should().BeEquivalentTo(before);
        await using var verification = database.CreateDbContext();
        (await verification.AuditLogs.CountAsync()).Should().Be(0);
    }

    private static object Payload(int bufferTimeMinutes = 25) => new
    {
        bufferTimeMinutes,
        defaultTravelSpeedKmh = 42.5,
        reroutingSearchRadiusKm = 12.5,
        weatherAlertThresholdSeverity = "moderate",
    };

    private static HttpClient CreateClient(TripMateApiFactory factory, User administrator)
    {
        var tokenService = factory.Services.GetRequiredService<IJwtTokenService>();
        return factory.CreateJwtClient(tokenService.GenerateAccessToken(administrator).Token);
    }

    private static async Task<User> SeedAdministratorAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var administrator = new User
        {
            Email = "algorithm-config-sql@example.com",
            FullName = "Algorithm Configuration Administrator",
            Role = UserRole.Administrator,
            Status = AccountStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        context.Users.Add(administrator);
        await context.SaveChangesAsync();
        return administrator;
    }

    private static async Task<List<SystemConfig>> ReadConfigurationsAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        return await context.SystemConfigs.AsNoTracking().OrderBy(row => row.ConfigKey).ToListAsync();
    }
}