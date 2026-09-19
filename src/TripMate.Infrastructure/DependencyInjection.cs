using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Authentication.PasswordReset;
using TripMate.Application.Features.TravelGroups.ManageInvitation;
using TripMate.Infrastructure.Authentication;
using TripMate.Infrastructure.Email;
using TripMate.Infrastructure.Persistence;
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
        services.AddSingleton<IGroupInvitationCodeGenerator, RandomGroupInvitationCodeGenerator>();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.Configure<PasswordResetSecurityOptions>(configuration.GetSection(PasswordResetSecurityOptions.SectionName));

        // UC-06 password reset: zero DB schema; reset state lives in process-local memory only.
        services.AddSingleton<IPasswordResetStateStore, InMemoryPasswordResetStateStore>();
        services.AddSingleton<IOtpCodeGenerator, CryptographicOtpCodeGenerator>();
        services.AddSingleton<IOtpProtectionService, HmacOtpProtectionService>();
        services.AddSingleton<IRequestTimingNormalizer, ResponseTimingNormalizer>();
        services.AddScoped<IPasswordResetEligibilityResolver, PasswordResetEligibilityResolver>();
        services.AddSingleton<ISmtpTransportFactory, MailKitSmtpClientFactory>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();

        services.AddDistributedMemoryCache();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddScoped<IPasswordHasherService, PasswordHasherService>();
        services.AddScoped<IPasswordHasher>(sp => sp.GetRequiredService<IPasswordHasherService>());
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddMemoryCache();
        services.AddScoped<IMessageService, MessageService>();
        services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();

        return services;
    }
}