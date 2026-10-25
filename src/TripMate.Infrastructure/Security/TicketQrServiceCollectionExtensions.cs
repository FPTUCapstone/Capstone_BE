using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Security;

public static class TicketQrServiceCollectionExtensions
{
    /// <summary>
    /// Registers the UC-29 QR codec. Missing or unusable keys stop the host at start-up in every
    /// environment rather than letting it issue tickets that cannot be verified (plan A2).
    /// </summary>
    public static IServiceCollection AddTicketQr(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TicketQrOptions>()
            .Bind(configuration.GetSection(TicketQrOptions.SectionName))
            .Validate(options => options.HasUsableKeys(), TicketQrOptions.ValidationMessage)
            .ValidateOnStart();
        services.AddSingleton<ITicketQrCodec, AesGcmHmacTicketQrCodec>();
        return services;
    }
}
