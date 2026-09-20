using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public enum ApiTestAuthenticationMode
{
    HeaderStub,
    JwtBearer,
}

public sealed class TripMateApiFactory(
    ApiTestAuthenticationMode authenticationMode = ApiTestAuthenticationMode.HeaderStub,
    string? sqlServerConnectionString = null,
    IReadOnlyList<string>? corsAllowedOrigins = null,
    string environmentName = "Testing",
    Func<IServiceProvider, IFirebaseAuthService>? firebaseServiceFactory = null,
    SaveChangesInterceptor? saveChangesInterceptor = null,
    Func<IServiceProvider, IDateTimeProvider>? dateTimeProviderFactory = null,
    Action<IServiceCollection>? configureTestServices = null,
    IInterceptor? dbInterceptor = null) : WebApplicationFactory<Program>
{
    internal const string JwtIssuer = "TripMate.Tests";
    internal const string JwtAudience = "TripMate.Tests";
    internal const string JwtSigningKey =
        "dGVzdC1vbmx5LXNpZ25pbmcta2V5LXRoYXQtaXMtbG9uZy1lbm91Z2g=";

    private readonly string _databaseName = $"tripmate-api-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environmentName);
        builder.UseSetting("Jwt:Issuer", JwtIssuer);
        builder.UseSetting("Jwt:Audience", JwtAudience);
        builder.UseSetting("Jwt:SigningKey", JwtSigningKey);

        if (corsAllowedOrigins is not null)
        {
            for (var index = 0; index < corsAllowedOrigins.Count; index++)
            {
                builder.UseSetting($"Cors:AllowedOrigins:{index}", corsAllowedOrigins[index]);
            }
        }

        if (sqlServerConnectionString is not null)
        {
            builder.UseSetting("ConnectionStrings:Default", sqlServerConnectionString);
        }
        else
        {
            // InMemory mode never opens a SQL connection, but Program.cs fails fast when
            // no connection string is configured (AGENTS.md §5.4 credential guard).
            builder.UseSetting("ConnectionStrings:Default", "Server=unused;Database=unused;");
        }

        builder.ConfigureTestServices(services =>
        {
            if (sqlServerConnectionString is null)
            {
                services.RemoveAll<ApplicationDbContext>();
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<IApplicationDbContext>();
                services.RemoveAll<ITravelGroupCreationLock>();
                services.RemoveAll<IGroupInvitationLock>();
                services.RemoveAll<IGroupJoinLock>();
                services.RemoveAll<ISchedulingRequestLock>();
                services.RemoveAll<IRouteDurationProvider>();

                services.AddDbContext<TestApiDbContext>(options =>
                {
                    options.UseInMemoryDatabase(_databaseName);

                    if (saveChangesInterceptor is not null)
                    {
                        options.AddInterceptors(saveChangesInterceptor);
                    }

                    if (dbInterceptor is not null)
                    {
                        options.AddInterceptors(dbInterceptor);
                    }
                });
                services.AddScoped<IApplicationDbContext>(provider =>
                    provider.GetRequiredService<TestApiDbContext>());

                services.AddScoped<ITravelGroupCreationLock, NoOpTravelGroupCreationLock>();
                services.AddScoped<IGroupInvitationLock, NoOpGroupInvitationLock>();
                services.AddScoped<IGroupJoinLock, NoOpGroupJoinLock>();
                services.AddScoped<ISchedulingRequestLock, NoOpSchedulingRequestLock>();
                services.AddScoped<IRouteDurationProvider, TestRouteDurationProvider>();
            }

            if (firebaseServiceFactory is not null)
            {
                services.RemoveAll<IFirebaseAuthService>();
                services.AddSingleton<IFirebaseAuthService>(sp => firebaseServiceFactory(sp));
            }

            if (dateTimeProviderFactory is not null)
            {
                services.RemoveAll<IDateTimeProvider>();
                services.AddSingleton<IDateTimeProvider>(sp => dateTimeProviderFactory(sp));
            }

            if (authenticationMode == ApiTestAuthenticationMode.HeaderStub)
            {
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
            }

            configureTestServices?.Invoke(services);
        });
    }

    public HttpClient CreateAuthenticatedClient(long userId, UserRole role)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        client.DefaultRequestHeaders.Add(
            TestAuthenticationHandler.UserIdHeader,
            userId.ToString());

        client.DefaultRequestHeaders.Add(
            TestAuthenticationHandler.RoleHeader,
            role.ToString());

        return client;
    }

    public HttpClient CreateJwtClient(string token)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    public async Task<T> WithDbContextAsync<T>(
        Func<TestApiDbContext, Task<T>> operation)
    {
        using var scope = Services.CreateScope();

        var context = scope.ServiceProvider
            .GetRequiredService<TestApiDbContext>();

        return await operation(context);
    }

}

public sealed class TestApiDbContext(DbContextOptions<TestApiDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<TravelerProfile> TravelerProfiles => Set<TravelerProfile>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PoiCategory> PoiCategories => Set<PoiCategory>();
    public DbSet<PointOfInterest> PointsOfInterest => Set<PointOfInterest>();
    public DbSet<PoiOpeningHour> PoiOpeningHours => Set<PoiOpeningHour>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PoiTag> PoiTags => Set<PoiTag>();
    public DbSet<OperatorProfile> OperatorProfiles => Set<OperatorProfile>();
    public DbSet<OperatorDocument> OperatorDocuments => Set<OperatorDocument>();
    public DbSet<Tour> Tours => Set<Tour>();
    public DbSet<TourSchedule> TourSchedules => Set<TourSchedule>();
    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<TourDestination> TourDestinations => Set<TourDestination>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PoiPhoto> PoiPhotos => Set<PoiPhoto>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<TravelGroup> TravelGroups => Set<TravelGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Itinerary> Itineraries => Set<Itinerary>();
    public DbSet<ItineraryItem> ItineraryItems => Set<ItineraryItem>();
    public DbSet<SchedulingRequest> SchedulingRequests => Set<SchedulingRequest>();
    public DbSet<TravelGroupCreationRequest> TravelGroupCreationRequests =>
        Set<TravelGroupCreationRequest>();
    public DbSet<GroupInvitation> GroupInvitations => Set<GroupInvitation>();
    public DbSet<GroupInvitationOperation> GroupInvitationOperations =>
        Set<GroupInvitationOperation>();
    public DbSet<GroupJoinOperation> GroupJoinOperations =>
        Set<GroupJoinOperation>();

    public async Task<int> RevokeRefreshTokenAsync(
        string tokenHash,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        var token = await RefreshTokens.SingleOrDefaultAsync(
            candidate => candidate.TokenHash == tokenHash && candidate.RevokedAtUtc == null,
            cancellationToken);
        if (token is null)
        {
            return 0;
        }

        token.RevokedAtUtc = revokedAtUtc;
        return 1;
    }

    public async Task<int> DeleteSignOutAuditEventsBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var audits = await AuditLogs
            .Where(audit =>
                audit.ActionType == TripMate.Domain.Common.AuditActionTypes.AuthSignOut &&
                audit.CreatedAtUtc < cutoffUtc)
            .ToListAsync(cancellationToken);
        AuditLogs.RemoveRange(audits);
        await SaveChangesAsync(cancellationToken);
        return audits.Count;
    }

    public Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        operation(cancellationToken);

    public Task<T> ExecuteInSerializableTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        operation(cancellationToken);

    public void ClearTrackedEntities() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ApplicationDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}

internal sealed class NoOpTravelGroupCreationLock : ITravelGroupCreationLock
{
    public Task AcquireAsync(
        long travelerUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class NoOpGroupInvitationLock : IGroupInvitationLock
{
    public Task AcquireAsync(
        long groupId,
        long travelerUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task AcquireCodeAsync(
        string inviteCode,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class NoOpGroupJoinLock : IGroupJoinLock
{
    public Task AcquireOperationLockAsync(long travelerUserId, Guid idempotencyKey, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task AcquireCodeLockAsync(string normalizedInviteCode, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task AcquireGroupLockAsync(long groupId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class NoOpSchedulingRequestLock : ISchedulingRequestLock
{
    public Task AcquireAsync(
        long travelerUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class TestRouteDurationProvider : IRouteDurationProvider
{
    public Task<RouteDurationMatrix> GetMatrixAsync(
        IReadOnlyList<RoutePoint> points,
        TransportMode transportMode,
        CancellationToken cancellationToken)
    {
        var durations = new int[points.Count, points.Count];
        for (var row = 0; row < points.Count; row++)
        {
            for (var column = 0; column < points.Count; column++)
            {
                durations[row, column] = row == column ? 0 : 15;
            }
        }

        return Task.FromResult(RouteDurationMatrix.Create(durations));
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

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            SchemeName);

        return Task.FromResult(
            AuthenticateResult.Success(ticket));
    }
}