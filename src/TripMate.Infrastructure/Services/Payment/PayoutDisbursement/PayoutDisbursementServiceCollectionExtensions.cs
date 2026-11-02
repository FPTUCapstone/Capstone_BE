using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Admin.PayoutSettlements.Processing;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.Services.Payment.PayoutDisbursement;

public static class PayoutDisbursementServiceCollectionExtensions
{
    /// <summary>Registers the UC-65 payout gateway selection and worker options. Invalid or unsafe
    /// settings fail at startup, before any request or sweep can run.</summary>
    public static IServiceCollection AddPayoutDisbursement(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PayoutDisbursementOptions>()
            .Bind(configuration.GetSection(PayoutDisbursementOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PayoutDisbursementOptions>, PayoutDisbursementOptionsValidator>();

        services.AddOptions<PayoutSettlementOptions>()
            .Bind(configuration.GetSection(PayoutSettlementOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PayoutSettlementOptions>, PayoutSettlementOptionsValidator>();
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<PayoutSettlementOptions>>().Value);

        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<PayoutDisbursementOptions>>().Value);

        services.AddScoped<MockDisbursementGateway>();
        services.AddHttpClient<PayOsPayoutGateway>(client => client.Timeout = TimeSpan.FromSeconds(30));
        // Mock unless the provider is explicitly PayOs; the options validator has already refused a
        // PayOs provider without the live-transfers switch and the payout credentials.
        services.AddScoped<IPayoutDisbursementGateway>(serviceProvider =>
            serviceProvider.GetRequiredService<PayoutDisbursementOptions>().UsesPayOs
                ? serviceProvider.GetRequiredService<PayOsPayoutGateway>()
                : serviceProvider.GetRequiredService<MockDisbursementGateway>());

        services.AddScoped<PayoutSettlementProcessor>();
        services.AddScoped<IPayoutSettlementSweeper>(serviceProvider =>
            serviceProvider.GetRequiredService<PayoutSettlementProcessor>());
        services.AddHostedService<PayoutSettlementBackgroundService>();

        return services;
    }
}