using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;

namespace TripMate.Api.IntegrationTests.Routes;

/// <summary>
/// Opt-in real browser host. Uses real JWT authentication and disposable SQL, with no
/// production authentication bypass. The external browser runner writes the completion marker.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class RouteBrowserHostSqlServerTests
{
    [RouteBrowserHostFact]
    [Trait("Category", "BrowserHost")]
    public async Task Browser_CreatesRouteThroughRealWebAndApi_ThenSqlConfirmsRouteAndAudit()
    {
        var artifactDirectory = Environment.GetEnvironmentVariable("TM101_BROWSER_ARTIFACT_DIR")
            ?? throw new InvalidOperationException("TM101_BROWSER_ARTIFACT_DIR is required.");
        Directory.CreateDirectory(artifactDirectory);
        var sessionPath = Path.Combine(artifactDirectory, "browser-session.json");
        var completePath = Path.Combine(artifactDirectory, "browser-complete");
        if (File.Exists(completePath))
        {
            throw new InvalidOperationException("Use a fresh browser completion marker path.");
        }

        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await CreateRouteSqlServerTests.SeedAsync(database);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        factory.StartServer();
        var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its loopback address.");
        var backendUrl = addresses.Addresses.Single();
        var jwt = factory.Services.GetRequiredService<IJwtTokenService>().GenerateAccessToken(seed.Staff);
        await File.WriteAllTextAsync(sessionPath, JsonSerializer.Serialize(new
        {
            backendUrl,
            accessToken = jwt.Token,
            userId = seed.Staff.Id,
            fromPoiId = seed.FromId,
            toPoiId = seed.ToId,
        }));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            while (!File.Exists(completePath))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
            }
            (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.RouteSegments WHERE status='Active';"))
                .Should().Be(1, "the browser must have committed exactly one server-confirmed creation");
            (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AuditLogs WHERE action_type='ROUTE_CREATE';"))
                .Should().Be(1, "the browser-created route must have an atomic audit event");
        }
        finally
        {
            File.Delete(sessionPath);
        }
    }
}

internal sealed class RouteBrowserHostFactAttribute : FactAttribute
{
    public RouteBrowserHostFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TM101_BROWSER_HOST") != "1")
        {
            Skip = "Opt-in browser host: set TM101_BROWSER_HOST=1 and TM101_BROWSER_ARTIFACT_DIR.";
        }
    }
}