using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Authentication.PasswordReset;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.TravelGroups.ManageInvitation;
using TripMate.Infrastructure.Authentication;
using TripMate.Infrastructure.Email;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Routing;
using TripMate.Infrastructure.Security;
using TripMate.Infrastructure.Services;

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
        services.AddScoped<ITravelGroupCreationLock, SqlServerTravelGroupCreationLock>();
        services.AddScoped<IGroupInvitationLock, SqlServerGroupInvitationLock>();
        services.AddScoped<IGroupJoinLock, SqlServerGroupJoinLock>();
        services.AddSingleton<IGroupInvitationCodeGenerator, RandomGroupInvitationCodeGenerator>();
        services.AddScoped<ISchedulingRequestLock, SqlServerSchedulingRequestLock>();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<OpenRouteServiceOptions>(
            configuration.GetSection(OpenRouteServiceOptions.SectionName));
        services.AddSingleton(
            configuration.GetSection(SchedulingGenerationOptions.SectionName)
                .Get<SchedulingGenerationOptions>()
            ?? new SchedulingGenerationOptions());
        services.AddHttpClient<OpenRouteServiceRouteDurationProvider>(
            (serviceProvider, client) =>
            {
                var routingOptions = serviceProvider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<OpenRouteServiceOptions>>()
                    .Value;
                client.BaseAddress = new Uri(routingOptions.BaseUrl, UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(20);
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