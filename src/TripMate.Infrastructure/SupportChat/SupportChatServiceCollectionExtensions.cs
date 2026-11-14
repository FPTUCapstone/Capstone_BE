using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Application.Features.SupportChat;

namespace TripMate.Infrastructure.SupportChat;

public static class SupportChatServiceCollectionExtensions
{
    public static IServiceCollection AddSupportChat(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SupportChatOptions>()
            .Bind(configuration.GetSection(SupportChatOptions.SectionName))
            .Validate(options => options.IsValid(), "SupportChat configuration is invalid.")
            .ValidateOnStart();
        services.AddHttpClient(GeminiSupportChatClient.HttpClientName, client =>
        {
            // The linked per-call deadline also covers waiting for the concurrency gate.
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<GeminiSupportChatClient>();
        services.AddSingleton<ISupportChatModel, GeminiSupportChatModel>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISupportChatPlanner, GeminiSupportChatPlanner>();
        services.AddScoped<ISupportChatLiveDataReader, SupportChatLiveDataReader>();
        services.AddSingleton<ISupportChatKnowledge, GeminiSupportChatKnowledge>();
        return services;
    }
}