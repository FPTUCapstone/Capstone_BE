using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Serilog;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services;

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
    Func<IServiceProvider, IEmailSender>? emailSenderFactory = null,
    SaveChangesInterceptor? saveChangesInterceptor = null,
    Func<IServiceProvider, IDateTimeProvider>? dateTimeProviderFactory = null,
    Action<IServiceCollection>? configureTestServices = null,
    IInterceptor? dbInterceptor = null,
    Func<IServiceProvider, IItineraryExplanationProvider>? explanationProviderFactory = null,
    bool? explanationProviderEnabled = null) : WebApplicationFactory<Program>
{
    internal const string JwtIssuer = "TripMate.Tests";
    internal const string JwtAudience = "TripMate.Tests";
    internal const string JwtSigningKey =
        "dGVzdC1vbmx5LXNpZ25pbmcta2V5LXRoYXQtaXMtbG9uZy1lbm91Z2g=";

    private readonly string _databaseName = $"tripmate-api-tests-{Guid.NewGuid():N}";
    private readonly string _logDirectory = Path.Combine(
        Path.GetTempPath(), "TripMate.Api.IntegrationTests", Guid.NewGuid().ToString("N"));
    private const string LogPathConfigurationKey = "Serilog:WriteTo:0:Args:path";

    public string LogFilePath => Services.GetRequiredService<IConfiguration>()[LogPathConfigurationKey]
        ?? throw new InvalidOperationException("This factory has no configured test file sink.");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environmentName);
        // Derived WithWebHostBuilder hosts need distinct files while their parent
        // host is alive. This factory owns all files through its disposal lifetime.
        string logPath = Path.Combine(_logDirectory, Guid.NewGuid().ToString("N"), "requests.log");
        builder.ConfigureAppConfiguration((context, configuration) =>
        {
            if (context.HostingEnvironment.IsEnvironment("Testing"))
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [LogPathConfigurationKey] = logPath,
                });
            }
        });
        builder.UseSetting("Jwt:Issuer", JwtIssuer);
        builder.UseSetting("Jwt:Audience", JwtAudience);
        builder.UseSetting("Jwt:SigningKey", JwtSigningKey);
        builder.UseSetting("PasswordResetSecurity:OtpPepper", "test-only-pepper-0123456789abcdef");
        builder.UseSetting("EmailVerification:ContinueUrl", "https://tripmate.test/verify-email");
        builder.UseSetting("Cloudinary:CloudName", "test-cloud");
        builder.UseSetting("Cloudinary:ApiKey", "test-api-key");
        builder.UseSetting("Cloudinary:ApiSecret", "test-api-secret");
        builder.UseSetting("Cloudinary:TourMediaFolderRoot", "tripmate/tests/tours");
        builder.UseSetting("Cloudinary:OperatorDocumentsFolderRoot", "tripmate/tests/operator-documents");
        // Integration tests must not inherit Development's external-provider enablement
        // or depend on developer User Secrets being present on the test host.
        builder.UseSetting("AiRanking:Enabled", "false");
        builder.UseSetting("AiExplanation:Enabled", "false");
        builder.UseSetting("SchedulingRateLimit:Provider", "SingleInstance");

        if (corsAllowedOrigins is not null)
        {
            for (var index = 0; index < corsAllowedOrigins.Count; index++)
            {
                builder.UseSetting($"Cors:AllowedOrigins:{index}", corsAllowedOrigins[index]);
            }
        }

        if (explanationProviderEnabled.HasValue)
        {
            builder.UseSetting(
                "AiExplanation:Enabled",
                explanationProviderEnabled.Value ? "true" : "false");
            if (explanationProviderEnabled.Value)
            {
                builder.UseSetting("AiExplanation:ApiKey", "test-explanation-api-key");
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
            // Own the logger through this host's DI lifetime. Disposing via the
            // static Log.Logger could instead close another live test host's sink.
            services.AddSerilog((provider, configuration) => configuration
                .ReadFrom.Configuration(provider.GetRequiredService<IConfiguration>())
                .ReadFrom.Services(provider)
                .WriteTo.Console(), preserveStaticLogger: true);

            // Ordinary API tests drive media recovery/cleanup explicitly when required.
            // Never let production timers race SQL fixtures or make ambient provider calls.
            for (var index = services.Count - 1; index >= 0; index--)
            {
                if (services[index].ServiceType == typeof(IHostedService)
                    && (services[index].ImplementationType == typeof(TripMate.Infrastructure.Reviews.Media.ReviewMediaRecoveryService)
                        || services[index].ImplementationType?.FullName
                            == "TripMate.Infrastructure.Services.TourMediaCleanupBackgroundService"))
                {
                    services.RemoveAt(index);
                }
            }

            if (sqlServerConnectionString is null)
            {
                services.RemoveAll<ApplicationDbContext>();
                services.RemoveAll<TripMate.Application.Features.TripReviews.Common.ITripReviewContextReader>();
                services.AddScoped<TripMate.Application.Features.TripReviews.Common.ITripReviewContextReader, UnsupportedInMemoryTripReviewContextReader>();
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<IApplicationDbContext>();
                services.RemoveAll<ITravelGroupCreationLock>();
                services.RemoveAll<IItineraryMutationLock>();
                services.RemoveAll<IGroupInvitationLock>();
                services.RemoveAll<IGroupJoinLock>();
                services.RemoveAll<ISchedulingRequestLock>();
                services.RemoveAll<ITourMediaUploadLock>();
                services.RemoveAll<ITourMediaCleanupOutboxStore>();
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
                // The media journal requires real SQL transactions/locks. InMemory
                // factories must not resolve it or pretend to prove SQL durability.
                services.RemoveAll<TripMate.Application.Features.TripReviews.Media.IReviewMediaJournal>();
                services.AddScoped<TripMate.Application.Features.TripReviews.Media.IReviewMediaJournal, UnsupportedInMemoryReviewMediaJournal>();
                services.RemoveAll<TripMate.Application.Features.TripReviews.Media.IReviewMediaRecoveryJournal>();
                services.RemoveAll<TripMate.Infrastructure.Reviews.Media.SqlServerReviewMediaJournal>();
                services.RemoveAll<TripMate.Application.Features.TripReviews.Media.ReviewMediaCoordinator>();
                services.RemoveAll<TripMate.Application.Features.TripReviews.Media.IReviewMediaCoordinator>();
                services.AddScoped<TripMate.Application.Features.TripReviews.Media.IReviewMediaCoordinator, UnsupportedInMemoryReviewMediaCoordinator>();
                services.RemoveAll<TripMate.Application.Features.TripReviews.Media.ReviewMediaRecoveryRunner>();
                services.RemoveAll<TripMate.Application.Features.TripReviews.Common.ITripReviewWriteLock>();
                services.AddScoped<TripMate.Application.Features.TripReviews.Common.ITripReviewWriteLock, NoOpTripReviewWriteLock>();

                services.AddScoped<ITravelGroupCreationLock, NoOpTravelGroupCreationLock>();
                services.AddScoped<IItineraryMutationLock, NoOpItineraryMutationLock>();
                services.AddScoped<IGroupInvitationLock, NoOpGroupInvitationLock>();
                services.AddScoped<IGroupJoinLock, NoOpGroupJoinLock>();
                services.AddScoped<ISchedulingRequestLock, NoOpSchedulingRequestLock>();
                services.AddScoped<ITourMediaUploadLock, NoOpTourMediaUploadLock>();
                services.AddScoped<ITourMediaCleanupOutboxStore, NoOpTourMediaCleanupOutboxStore>();
                services.AddScoped<IRouteDurationProvider, TestRouteDurationProvider>();
            }
            else if (saveChangesInterceptor is not null || dbInterceptor is not null)
            {
                services.RemoveAll<ApplicationDbContext>();
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<IApplicationDbContext>();

                services.AddDbContext<ApplicationDbContext>(options =>
                {
                    options.UseSqlServer(sqlServerConnectionString);

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
                    provider.GetRequiredService<ApplicationDbContext>());
            }

            if (firebaseServiceFactory is not null)
            {
                services.RemoveAll<IFirebaseAuthService>();
                services.AddSingleton<IFirebaseAuthService>(sp => firebaseServiceFactory(sp));
            }

            if (emailSenderFactory is not null)
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sp => emailSenderFactory(sp));
            }

            if (dateTimeProviderFactory is not null)
            {
                services.RemoveAll<IDateTimeProvider>();
                services.AddSingleton<IDateTimeProvider>(sp => dateTimeProviderFactory(sp));
            }

            services.AddScoped(provider => new PoiRankingOrchestrator(
                new PersonalBehaviorFeatureAggregator(
                    provider.GetRequiredService<IApplicationDbContext>()),
                ProviderDisabledPoiRankingProvider.Instance,
                new PersonalizationRankingOptions(),
                providerEnabled: false,
                provider.GetRequiredService<ILogger<PoiRankingOrchestrator>>()));

            if (explanationProviderFactory is not null)
            {
                services.RemoveAll<IItineraryExplanationProvider>();
                services.AddScoped<IItineraryExplanationProvider>(sp => explanationProviderFactory(sp));
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            DeleteTestLogs();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        DeleteTestLogs();
    }

    private void DeleteTestLogs()
    {
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }

}

internal sealed class UnsupportedInMemoryTripReviewContextReader
    : TripMate.Application.Features.TripReviews.Common.ITripReviewContextReader
{
    public Task<TripMate.Application.Features.TripReviews.Common.TripReviewContextData?> ReadOwnedAsync(
        long bookingId, long travelerUserId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Booking evidence requires the isolated SQL Server test fixture.");

    public Task<TripMate.Application.Features.TripReviews.Common.TripReviewContextData?> ReadOwnedForUpdateAsync(
        long bookingId, long travelerUserId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Locked booking evidence requires the isolated SQL Server test fixture.");
}

internal sealed class UnsupportedInMemoryReviewMediaJournal
    : TripMate.Application.Features.TripReviews.Media.IReviewMediaJournal
{
    public Task<TripMate.Application.Common.Models.Result<IReadOnlyList<TripMate.Application.Features.TripReviews.Media.ReviewMediaVersion>>> ReserveAsync(
        long bookingId, long travelerId, IReadOnlyList<TripMate.Domain.Entities.TripReviewMediaOperation> operations, CancellationToken ct) =>
        throw new NotSupportedException("Media journal requires the isolated SQL Server test fixture.");

    public Task<TripMate.Application.Common.Models.Result> AdoptAsync(
        Guid batchId, long travelerId, IReadOnlyList<TripMate.Application.Features.TripReviews.Media.ReviewMediaVersion> expected,
        TripMate.Domain.Entities.TripReview parent, DateTimeOffset now, CancellationToken ct) =>
        throw new NotSupportedException("Media journal requires the isolated SQL Server test fixture.");

    public Task<TripMate.Application.Common.Models.Result> MarkCleanupPendingAsync(
        Guid batchId, long bookingId, long travelerId, IReadOnlyList<TripMate.Application.Features.TripReviews.Media.ReviewMediaVersion> expected,
        DateTimeOffset now, CancellationToken ct) =>
        throw new NotSupportedException("Media journal requires the isolated SQL Server test fixture.");
}

internal sealed class UnsupportedInMemoryReviewMediaCoordinator
    : TripMate.Application.Features.TripReviews.Media.IReviewMediaCoordinator
{
    public Task<TripMate.Application.Common.Models.Result<TripMate.Application.Features.TripReviews.Media.PreparedReviewMedia>> PrepareAsync(
        long bookingId, long travelerId, IReadOnlyList<TripMate.Application.Features.TripReviews.Media.ReviewImageSource> images, CancellationToken ct) =>
        throw new NotSupportedException("Media coordinator requires the isolated SQL Server test fixture.");
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
    public DbSet<TourMedia> TourMedia => Set<TourMedia>();
    public DbSet<TourMediaUploadOperation> TourMediaUploadOperations =>
        Set<TourMediaUploadOperation>();
    public DbSet<TourMediaCleanupOutboxItem> TourMediaCleanupOutbox =>
        Set<TourMediaCleanupOutboxItem>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PoiPhoto> PoiPhotos => Set<PoiPhoto>();
    public DbSet<Review> Reviews => Set<Review>();

    public DbSet<TripReview> TripReviews => Set<TripReview>();
    public DbSet<TripReviewMedia> TripReviewMedia => Set<TripReviewMedia>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<TripMate.Domain.Entities.ServiceProvider> ServiceProviders =>
        Set<TripMate.Domain.Entities.ServiceProvider>();
    public DbSet<CommercialService> CommercialServices => Set<CommercialService>();
    public DbSet<TravelGroup> TravelGroups => Set<TravelGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Itinerary> Itineraries => Set<Itinerary>();
    public DbSet<ItineraryItem> ItineraryItems => Set<ItineraryItem>();
    public DbSet<SchedulingRequest> SchedulingRequests => Set<SchedulingRequest>();
    public DbSet<RecommendationBehaviorEvent> RecommendationBehaviorEvents =>
        Set<RecommendationBehaviorEvent>();
    public DbSet<TravelGroupCreationRequest> TravelGroupCreationRequests =>
        Set<TravelGroupCreationRequest>();
    public DbSet<GroupInvitation> GroupInvitations => Set<GroupInvitation>();
    public DbSet<GroupInvitationOperation> GroupInvitationOperations =>
        Set<GroupInvitationOperation>();
    public DbSet<GroupJoinOperation> GroupJoinOperations =>
        Set<GroupJoinOperation>();
    public DbSet<ItineraryVersionOperation> ItineraryVersionOperations =>
        Set<ItineraryVersionOperation>();

    public DbSet<TripSession> TripSessions => Set<TripSession>();

    public DbSet<Incident> Incidents => Set<Incident>();

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

    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();

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

internal sealed class ProviderDisabledPoiRankingProvider : IPoiRankingProvider
{
    public static readonly ProviderDisabledPoiRankingProvider Instance = new();

    public Task<Result<PoiRankingResult>> RankAsync(
        PoiRankingRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The provider-disabled test path cannot call a provider.");
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

internal sealed class NoOpTripReviewWriteLock : TripMate.Application.Features.TripReviews.Common.ITripReviewWriteLock
{
    public Task AcquireAsync(long bookingId, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class NoOpTourMediaUploadLock : ITourMediaUploadLock
{
    public Task AcquireOperationAsync(long tourId, long actorUserId, Guid idempotencyKey,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AcquireTourAsync(long tourId, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class NoOpTourMediaCleanupOutboxStore : ITourMediaCleanupOutboxStore
{
    public Task<IReadOnlyList<TourMediaCleanupClaim>> ClaimDueAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TourMediaCleanupClaim>>([]);

    public Task CompleteAsync(long id, Guid leaseToken, DateTimeOffset completedAtUtc, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RetryAsync(
        long id,
        Guid leaseToken,
        string safeErrorCode,
        DateTimeOffset notBeforeUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ExhaustAsync(
        long id,
        Guid leaseToken,
        string safeErrorCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class NoOpItineraryMutationLock : IItineraryMutationLock
{
    public Task AcquireAsync(long itineraryId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
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