using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Infrastructure.Authentication;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Routing;
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
        services.AddScoped<IGoogleTokenValidator, GoogleTokenValidator>();

        return services;
    }
}
