using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class TripMateApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"tripmate-api-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "TripMate.Tests",
                ["Jwt:Audience"] = "TripMate.Tests",
                ["Jwt:SigningKey"] =
                    "dGVzdC1vbmx5LXNpZ25pbmcta2V5LXRoYXQtaXMtbG9uZy1lbm91Z2g=",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ApplicationDbContext>();
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IApplicationDbContext>();

            services.AddDbContext<TestApiDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
            services.AddScoped<IApplicationDbContext>(provider =>
                provider.GetRequiredService<TestApiDbContext>());

            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    _ => { });
        });
    }

    public HttpClient CreateAuthenticatedClient(long userId, UserRole role)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserIdHeader, userId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RoleHeader, role.ToString());
        return client;
    }

    public async Task<T> WithDbContextAsync<T>(Func<TestApiDbContext, Task<T>> operation)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestApiDbContext>();
        return await operation(context);
    }
}

public sealed class TestApiDbContext(DbContextOptions<TestApiDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PoiCategory> PoiCategories => Set<PoiCategory>();
    public DbSet<PointOfInterest> PointsOfInterest => Set<PointOfInterest>();
    public DbSet<PoiOpeningHour> PoiOpeningHours => Set<PoiOpeningHour>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PoiTag> PoiTags => Set<PoiTag>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        operation(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}

internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserIdHeader = "X-Test-User-Id";
    public const string RoleHeader = "X-Test-Role";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserIdHeader, out var userId)
            || !Request.Headers.TryGetValue(RoleHeader, out var role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role.ToString()),
        ],
        SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
