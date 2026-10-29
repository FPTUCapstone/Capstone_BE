using Microsoft.AspNetCore.DataProtection;
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
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.PointsOfInterest.ExternalReviews;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.Features.TravelGroups.ManageInvitation;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiExplanation;
using TripMate.Infrastructure.AiRanking;
using TripMate.Infrastructure.Authentication;
using TripMate.Infrastructure.Email;
using TripMate.Infrastructure.ExternalReviews;
using TripMate.Infrastructure.Media;
using TripMate.Infrastructure.Media.Cloudinary;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Reporting;
using TripMate.Infrastructure.Reviews.Media;
using TripMate.Infrastructure.Reviews.Moderation;
using TripMate.Infrastructure.Routing;
using TripMate.Infrastructure.Security;
using TripMate.Infrastructure.Services;
using TripMate.Infrastructure.Services.Payment;
using TripMate.Infrastructure.Services.Redis;
using TripMate.Infrastructure.TripAlerts;
using TripMate.Infrastructure.Weather;

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
        services.AddOptions<TripAlertOptions>()
            .Bind(configuration.GetSection(TripAlertOptions.SectionName))
            .Validate(options => options.ScanInterval > TimeSpan.Zero, "Trip alert scan interval must be positive.")
            .Validate(options => options.ScheduleDelayThreshold > TimeSpan.Zero, "Delay threshold must be positive.")
            .Validate(options => options.DeviationCooldown > TimeSpan.Zero, "Deviation cooldown must be positive.")
            .Validate(options => options.DeliveryLease > TimeSpan.Zero, "Delivery lease must be positive.")
            .Validate(options => options.AdvisoryRainMmPerHour >= 0
                && options.SevereRainMmPerHour > options.AdvisoryRainMmPerHour
                && options.ExtremeRainMmPerHour > options.SevereRainMmPerHour,
                "Weather thresholds are invalid.")
            .ValidateOnStart();
        services.AddMemoryCache();
        services.AddOptions<TomorrowWeatherOptions>()
            .Bind(configuration.GetSection(TomorrowWeatherOptions.SectionName))
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey),
                "Tomorrow API key is required when weather integration is enabled.")
            .Validate(options => options.CacheDuration > TimeSpan.Zero
                && options.CacheDuration <= TimeSpan.FromMinutes(10),
                "Tomorrow cache duration must be at most ten minutes.")
            .ValidateOnStart();
        services.AddHttpClient<ITripWeatherProvider, TomorrowTripWeatherProvider>((provider, client) =>
        {
            var weather = provider.GetRequiredService<IOptions<TomorrowWeatherOptions>>().Value;
            client.BaseAddress = new Uri(weather.BaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<AlertDeliveryQueue>();
        services.AddScoped<FirebaseTripAlertPushSender>();
        services.AddHostedService<TripAlertScanBackgroundService>();
        services.AddHostedService<TripAlertDeliveryBackgroundService>();
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
        services.AddSingleton<IBookingCancellationPersistenceErrorClassifier,
            SqlServerBookingCancellationPersistenceErrorClassifier>();
        services.AddSingleton<IPayoutPersistenceErrorClassifier, SqlServerPayoutPersistenceErrorClassifier>();
        services.AddScoped<IRefundManualCompletionLock, SqlServerRefundManualCompletionLock>();
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
        services.AddSingleton<ITravelerAvatarStorage, CloudinaryTravelerAvatarStorage>();
        services.AddSingleton<SqlTravelerAvatarCleanupJournal>();
        services.AddSingleton<ITravelerAvatarCleanupJournal>(sp =>
            sp.GetRequiredService<SqlTravelerAvatarCleanupJournal>());
        services.AddHostedService<TravelerAvatarCleanupBackgroundService>();
        services.AddSingleton<IOperatorDocumentStorage, CloudinaryOperatorDocumentStorage>();
        services.AddSingleton<IRevenueExportStorage, CloudinaryRevenueExportStorage>();
        services.AddSingleton<IRevenueExportRenderer, XlsxRevenueExportRenderer>();

        services.AddOptions<RevenueExportOptions>()
            .Bind(configuration.GetSection(RevenueExportOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RevenueExportOptions>>().Value);
        services.AddScoped<TripMate.Application.Features.Operator.Revenue.Exports.Worker.RevenueExportWorkerProcessor>();
        services.AddScoped<TripMate.Application.Features.Operator.Revenue.Exports.Worker.RevenueExportExpiryProcessor>();
        services.AddHostedService<RevenueExportWorkerBackgroundService>();
        services.AddHostedService<RevenueExportExpiryBackgroundService>();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddOptions<OpenRouteServiceOptions>()
            .Bind(configuration.GetSection(OpenRouteServiceOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OpenRouteServiceOptions>, OpenRouteServiceOptionsValidator>();
        services.AddOptions<SchedulingGenerationOptions>()
            .Bind(configuration.GetSection(SchedulingGenerationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<SchedulingGenerationOptions>,
            SchedulingGenerationOptionsValidator>();
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IOptions<SchedulingGenerationOptions>>()
            .Value);
        services.AddOptions<TimeOfDayOptions>()
            .Bind(configuration.GetSection(TimeOfDayOptions.SectionName))
            .Validate(options => options.MaxExtraMinutes is >= 0 and <= 120, "TimeOfDay:MaxExtraMinutes must be between 0 and 120.")
            .ValidateOnStart();
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IOptions<TimeOfDayOptions>>()
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
        services.AddOptions<TourBookingOptions>()
            .Bind(configuration.GetSection(TourBookingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TourBookingOptions>, TourBookingOptionsValidator>();
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<TourBookingOptions>>().Value);
        services.AddHostedService<PendingBookingExpiryBackgroundService>();

        services.AddOptions<RefundProcessingOptions>()
            .Bind(configuration.GetSection(RefundProcessingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RefundProcessingOptions>, RefundProcessingOptionsValidator>();
        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IOptions<RefundProcessingOptions>>().Value);
        services.AddScoped<RefundProcessor>();
        services.AddHostedService<RefundProcessingBackgroundService>();

        services.AddOptions<PaymentOptions>()
            .Bind(configuration.GetSection(PaymentOptions.SectionName));
        var legacyPayOs = configuration.GetSection(PaymentOptionsConfiguration.LegacyPayOsSectionName)
            .Get<PayOsOptions>();
        services.PostConfigure<PaymentOptions>(options =>
            PaymentOptionsConfiguration.ApplyLegacyPayOs(options, legacyPayOs));
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<PaymentOptions>>().Value);
        services.AddScoped<IPaymentCompletionProcessor, PaymentCompletionProcessor>();

        var paymentConfig = PaymentOptionsConfiguration.Read(configuration);
        // Checkout uses the configured provider. Refunds use the provider persisted on the
        // original transaction, so keep both supported adapters resolvable independently.
        services.AddHttpClient<PayOsPaymentGateway>();
        services.AddScoped<MockPaymentGateway>();
        services.AddScoped<IRefundPaymentGatewayResolver, RefundPaymentGatewayResolver>();
        services.AddScoped<IPaymentGateway>(serviceProvider =>
            string.Equals(paymentConfig.Provider, "PayOS", StringComparison.OrdinalIgnoreCase)
                ? serviceProvider.GetRequiredService<PayOsPaymentGateway>()
                : serviceProvider.GetRequiredService<MockPaymentGateway>());
        services.AddDataProtection();
        services.AddSingleton<IPiiProtector, DataProtectionPiiProtector>();
        services.AddSingleton<IPushTokenProtector, DataProtectionPushTokenProtector>();
        services.AddOptions<NavigationSessionOptions>()
            .Bind(configuration.GetSection(NavigationSessionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NavigationSessionOptions>, NavigationSessionOptionsValidator>();
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<NavigationSessionOptions>>().Value);
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
        services.AddPoiRankingHttpProvider(configuration);
        services.AddOptions<ExplanationProviderOptions>()
            .Bind(configuration.GetSection(ExplanationProviderOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<ExplanationProviderOptions>,
            ExplanationProviderOptionsValidator>();
        services.AddItineraryExplanationHttpProvider(configuration);
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

        // REVIEW2: Tripadvisor (Terra) ratings/reviews and on-demand review translation (Gemini).
        services.Configure<TripadvisorOptions>(configuration.GetSection(TripadvisorOptions.SectionName));
        services.AddSingleton<TripadvisorCallBudget>();
        services.AddHttpClient<ITripadvisorClient, TerraTripadvisorClient>((serviceProvider, client) =>
        {
            var tripadvisorOptions = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<TripadvisorOptions>>()
                .Value;
            client.BaseAddress = new Uri(tripadvisorOptions.BaseUrl, UriKind.Absolute);
        });
        services.Configure<ReviewTranslationOptions>(configuration.GetSection(ReviewTranslationOptions.SectionName));
        services.AddHttpClient<IReviewTranslator, GeminiReviewTranslator>();
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.Configure<PasswordResetSecurityOptions>(configuration.GetSection(PasswordResetSecurityOptions.SectionName));

        // UC-06 password reset: zero DB schema; reset state lives in process-local memory only.
        services.AddSingleton<IPasswordResetStateStore, InMemoryPasswordResetStateStore>();
        services.AddSingleton<IPasswordResetAccountLock, InMemoryPasswordResetAccountLock>();

        // UC-07 change password: sp_getapplock (scoped, like ApplicationDbContext) serializes
        // concurrent attempts for one account across every process/container — not only within
        // one in-memory singleton — so a second request cannot revoke a session the first just
        // issued.
        services.AddScoped<IAccountCredentialLock, SqlServerAccountCredentialLock>();
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