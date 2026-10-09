using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.Authentication.PasswordReset;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.TravelGroups.ManageInvitation;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiExplanation;
using TripMate.Infrastructure.AiRanking;
using TripMate.Infrastructure.Authentication;
using TripMate.Infrastructure.Email;
using TripMate.Infrastructure.Media;
using TripMate.Infrastructure.Media.Cloudinary;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Reviews.Media;
using TripMate.Infrastructure.Reviews.Moderation;
using TripMate.Infrastructure.Routing;
using TripMate.Infrastructure.Security;
using TripMate.Infrastructure.Services;
using TripMate.Infrastructure.Services.Redis;

namespace TripMate.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Database-first: the schema is owned by database/tripmate_schema_v7.sql, applied via
        // database/apply-schema.sh. This context only maps to it — no EF Core migrations here.
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("Default")));

        services.AddScoped<IApplicationDbContext>(sp =>
            sp.GetRequiredService<ApplicationDbContext>());
        services.AddSingleton<IOperatorRegistrationConstraintClassifier,
            OperatorRegistrationConstraintClassifier>();
        services.AddScoped<ITravelGroupCreationLock, SqlServerTravelGroupCreationLock>();
        services.AddScoped<IItineraryMutationLock, SqlServerItineraryMutationLock>();
        services.AddScoped<IItineraryAccessService, ItineraryAccessService>();
        services.AddScoped<IItineraryVersionService, ItineraryVersionService>();
        services.AddScoped<IGroupInvitationLock, SqlServerGroupInvitationLock>();
        services.AddScoped<IGroupJoinLock, SqlServerGroupJoinLock>();
        services.AddSingleton<IGroupInvitationCodeGenerator, RandomGroupInvitationCodeGenerator>();
        services.AddScoped<ISchedulingRequestLock, SqlServerSchedulingRequestLock>();
        services.AddScoped<ITripReviewContextReader, SqlServerTripReviewContextReader>();
        services.AddScoped<ITripReviewWriteLock, SqlServerTripReviewWriteLock>();
        services.AddSingleton<ITripReviewPersistenceErrorClassifier, SqlServerTripReviewPersistenceErrorClassifier>();
        services.AddSingleton<IReviewContentModerator, LocalReviewContentModerator>();
        services.AddSingleton<IReviewImageInspector, ReviewImageInspector>();
        services.AddScoped<SqlServerReviewMediaJournal>();
        services.AddScoped<IReviewMediaJournal>(sp => sp.GetRequiredService<SqlServerReviewMediaJournal>());
        services.AddScoped<IReviewMediaRecoveryJournal>(sp => sp.GetRequiredService<SqlServerReviewMediaJournal>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<ReviewCloudinaryOptions>()
            .Bind(configuration.GetSection("Cloudinary"))
            .Validate(
                options => options.ReviewMediaFolderRoot == ReviewCloudinaryOptions.StableReviewMediaFolderRoot,
                "Cloudinary review media root is immutable for this schema version.")
            .ValidateOnStart();
        services.AddSingleton<IReviewCloudinaryClient, ReviewCloudinarySdkClient>();
        services.AddSingleton<IReviewMediaStorage, CloudinaryReviewMediaStorage>();
        services.AddScoped<ReviewMediaCoordinator>();
        services.AddScoped<IReviewMediaCoordinator>(sp => sp.GetRequiredService<ReviewMediaCoordinator>());
        services.AddScoped<ReviewMediaRecoveryRunner>();
        services.AddHostedService<ReviewMediaRecoveryService>();

        services.AddScoped<ITourMediaUploadLock, SqlServerTourMediaUploadLock>();
        services.AddScoped<ITourMediaCleanupOutboxStore, SqlServerTourMediaCleanupOutboxStore>();
        services.AddScoped<TourMediaCleanupProcessor>();
        services.AddSingleton<SqlOperatorDocumentCleanupJournal>();
        services.AddSingleton<IOperatorDocumentCleanupJournal>(sp =>
            sp.GetRequiredService<SqlOperatorDocumentCleanupJournal>());
        services.AddHostedService<OperatorDocumentCleanupBackgroundService>();

        services.AddOptions<TourMediaCleanupOptions>()
            .Bind(configuration.GetSection(TourMediaCleanupOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHostedService<TourMediaCleanupBackgroundService>();

        services.AddOptions<CloudinaryOptions>()
            .Bind(configuration.GetSection(CloudinaryOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CloudinaryOptions>, CloudinaryOptionsValidator>();
        services.AddSingleton<ICloudinaryClient, CloudinarySdkClient>();
        services.AddSingleton<ITourMediaImageInspector, SkiaSharpTourMediaImageInspector>();
        services.AddSingleton<ITourMediaStorage, CloudinaryTourMediaStorage>();
        services.AddSingleton<IOperatorDocumentStorage, CloudinaryOperatorDocumentStorage>();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<OpenRouteServiceOptions>(
            configuration.GetSection(OpenRouteServiceOptions.SectionName));
        services.AddOptions<SchedulingGenerationOptions>()
            .Bind(configuration.GetSection(SchedulingGenerationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<SchedulingGenerationOptions>,
            SchedulingGenerationOptionsValidator>();
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IOptions<SchedulingGenerationOptions>>()
            .Value);
        services.AddOptions<SchedulingReservationOptions>()
            .Bind(configuration.GetSection(SchedulingReservationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<SchedulingReservationOptions>,
            SchedulingReservationOptionsValidator>();
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IOptions<SchedulingReservationOptions>>()
            .Value);
        services.AddOptions<SchedulingRateLimitOptions>()
            .Bind(configuration.GetSection(SchedulingRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SchedulingRateLimitOptions>, SchedulingRateLimitOptionsValidator>();
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<SchedulingRateLimitOptions>>().Value);
        services.AddSingleton<GenerateRateLimiterMetrics>();

        var rateLimitOptions = configuration.GetSection(SchedulingRateLimitOptions.SectionName)
            .Get<SchedulingRateLimitOptions>() ?? new SchedulingRateLimitOptions();
        if (rateLimitOptions.Provider == SchedulingRateLimitProvider.Redis)
        {
            services.AddSingleton<IConnectionMultiplexer>(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<SchedulingRateLimitOptions>();
                var connectionString = configuration.GetConnectionString(
                    options.RedisConnectionStringName);
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException(
                        $"Redis rate limit provider requires a valid connection string named '{options.RedisConnectionStringName}'.");
                }

                var redisOptions = ConfigurationOptions.Parse(connectionString);
                redisOptions.AbortOnConnectFail = false;
                int timeoutMilliseconds = (int)Math.Max(
                    1000,
                    options.CommandTimeout.TotalMilliseconds);
                redisOptions.ConnectTimeout = timeoutMilliseconds;
                redisOptions.SyncTimeout = timeoutMilliseconds;
                redisOptions.AsyncTimeout = timeoutMilliseconds;
                return ConnectionMultiplexer.Connect(redisOptions);
            });
            services.AddSingleton<IGenerateRateLimiter, RedisGenerateRateLimiter>();
        }
        else
        {
            services.AddSingleton<IGenerateRateLimiter, InMemoryGenerateRateLimiter>();
        }
        services.AddOptions<PersonalizationRankingOptions>()
            .Bind(configuration.GetSection(PersonalizationRankingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<PersonalizationRankingOptions>,
            PersonalizationRankingOptionsValidator>();
        services.AddOptions<PoiRankingProviderOptions>()
            .Bind(configuration.GetSection(PoiRankingProviderOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<PoiRankingProviderOptions>,
            PoiRankingProviderOptionsValidator>();
        services.AddSingleton(serviceProvider => new InMemoryAiProviderBudget(
            serviceProvider.GetRequiredService<IDateTimeProvider>(),
            new AiProviderBudgetSettings(
                serviceProvider.GetRequiredService<IOptions<PoiRankingProviderOptions>>().Value.RateLimitPerMinute,
                serviceProvider.GetRequiredService<IOptions<PoiRankingProviderOptions>>().Value.MaxConcurrency),
            new AiProviderBudgetSettings(
                serviceProvider.GetRequiredService<IOptions<ExplanationProviderOptions>>().Value.RateLimitPerMinute,
                serviceProvider.GetRequiredService<IOptions<ExplanationProviderOptions>>().Value.MaxConcurrency)));
        services.AddSingleton<IAiProviderBudget>(sp => sp.GetRequiredService<InMemoryAiProviderBudget>());
        services.AddHttpClient<IPoiRankingProvider, HttpPoiRankingProvider>();
        services.AddOptions<ExplanationProviderOptions>()
            .Bind(configuration.GetSection(ExplanationProviderOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<ExplanationProviderOptions>,
            ExplanationProviderOptionsValidator>();
        services.AddHttpClient<
            IItineraryExplanationProvider,
            HttpItineraryExplanationProvider>();
        services.AddOptions<ItineraryExplanationExecutionOptions>()
            .Configure<IOptions<ExplanationProviderOptions>>((executionOptions, providerOptions) =>
            {
                ExplanationProviderOptions options = providerOptions.Value;
                executionOptions.Enabled = options.Enabled;
                executionOptions.ProviderTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                executionOptions.OverallTimeout = TimeSpan.FromSeconds(options.OverallTimeoutSeconds);
                executionOptions.MaxAttempts = options.MaxAttempts;
                executionOptions.RetryBaseDelayMilliseconds = options.RetryBaseDelayMilliseconds;
            })
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<ItineraryExplanationExecutionOptions>,
            ItineraryExplanationExecutionOptionsValidator>();
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IOptions<ItineraryExplanationExecutionOptions>>()
            .Value);
        services.AddScoped<PersonalBehaviorFeatureAggregator>();
        services.AddScoped(serviceProvider => new PoiRankingOrchestrator(
            serviceProvider.GetRequiredService<PersonalBehaviorFeatureAggregator>(),
            serviceProvider.GetRequiredService<IPoiRankingProvider>(),
            serviceProvider.GetRequiredService<IOptions<PersonalizationRankingOptions>>().Value,
            serviceProvider.GetRequiredService<IOptions<PoiRankingProviderOptions>>().Value.Enabled,
            serviceProvider.GetRequiredService<ILogger<PoiRankingOrchestrator>>()));
        services.AddHttpClient<OpenRouteServiceRouteDurationProvider>(
            (serviceProvider, client) =>
            {
                var routingOptions = serviceProvider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<OpenRouteServiceOptions>>()
                    .Value;
                client.BaseAddress = new Uri(routingOptions.BaseUrl, UriKind.Absolute);
                client.Timeout = SchedulingReservationOptions.RouteProviderTimeout;
            });
        services.AddScoped<IRouteDurationProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<OpenRouteServiceRouteDurationProvider>());
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.Configure<PasswordResetSecurityOptions>(configuration.GetSection(PasswordResetSecurityOptions.SectionName));

        // UC-06 password reset: zero DB schema; reset state lives in process-local memory only.
        services.AddSingleton<IPasswordResetStateStore, InMemoryPasswordResetStateStore>();
        services.AddSingleton<IPasswordResetAccountLock, InMemoryPasswordResetAccountLock>();
        services.AddSingleton<IOtpCodeGenerator, CryptographicOtpCodeGenerator>();
        services.AddSingleton<IOtpProtectionService, HmacOtpProtectionService>();
        services.AddSingleton<IRequestTimingNormalizer, ResponseTimingNormalizer>();
        services.AddSingleton<PasswordResetEmailQueue>();
        services.AddSingleton<IPasswordResetEmailQueue>(sp =>
            sp.GetRequiredService<PasswordResetEmailQueue>());
        services.AddHostedService<PasswordResetEmailDeliveryService>();
        services.AddScoped<IPasswordResetEligibilityResolver, PasswordResetEligibilityResolver>();
        services.AddSingleton<ISmtpTransportFactory, MailKitSmtpClientFactory>();
        services.AddScoped<SmtpEmailSender>();
        services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<SmtpEmailSender>());
        services.AddScoped<IEmailVerificationSender>(sp => sp.GetRequiredService<SmtpEmailSender>());
        services.AddSingleton<IEmailVerificationResendCooldown, EmailVerificationResendCooldown>();
        services.AddSingleton<FirebaseEmailVerificationLinkService>();
        services.AddSingleton<IEmailVerificationLinkService>(sp =>
            sp.GetRequiredService<FirebaseEmailVerificationLinkService>());
        services.AddSingleton<IEmailVerificationStatusService>(sp =>
            sp.GetRequiredService<FirebaseEmailVerificationLinkService>());

        services.AddDistributedMemoryCache();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddScoped<IAuditFailureRecorder, AuditFailureRecorder>();
        services.AddScoped<IPasswordHasherService, PasswordHasherService>();
        services.AddScoped<IPasswordHasher>(sp => sp.GetRequiredService<IPasswordHasherService>());
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddMemoryCache();
        services.AddScoped<IMessageService, MessageService>();
        services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();

        // UC-05 Sign Out: Audit service
        services.AddScoped<IAuditService, AuditService>();

        // UC-05 Sign Out: Audit retention cleanup job
        services.AddHostedService<AuditRetentionCleanupService>();

        return services;
    }
}