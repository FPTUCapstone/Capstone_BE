using System.Security.Cryptography;
using System.Text;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.DevSearch;
using TripMate.Application.Features.Tours.SemanticPoc;
using TripMate.Infrastructure.Services.SemanticPoc;

namespace TripMate.Api.DevSearch;

public static class DevTourAccess
{
    public const string Header = "X-TM70-Dev-Key";
    public static bool Enabled(string environment, bool enabled) => enabled && environment is "Development" or "Staging";
    public static bool Allows(string environment, bool enabled, string? expected, string? supplied)
        => Enabled(environment, enabled) && expected?.Length >= 32 && supplied is not null
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
}

public static class DevTourSearchRegistration
{
    public static IServiceCollection AddDevTourSearch(this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        if (!DevTourAccess.Enabled(environment.EnvironmentName, config.GetValue<bool>("DevTourSearch:Enabled"))) return services;
        if (config["DevTourSearch:Key"]?.Length is not >= 32) throw new InvalidOperationException("A separate strong development access key is required.");
        static Uri LocalUrl(string? raw)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http"
                || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidOperationException("The local dev search dependencies must use explicit loopback HTTP URLs.");
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        }
        var embedding = LocalUrl(config["DevTourSearch:EmbeddingUrl"]);
        var qdrant = LocalUrl(config["DevTourSearch:QdrantUrl"]);
        var collection = config["DevTourSearch:Collection"] ?? "";
        if (!collection.StartsWith("tm70_poc_dev_", StringComparison.Ordinal))
            throw new InvalidOperationException("A dedicated development collection is required.");
        var k = config.GetValue("DevTourSearch:RrfK", 60);
        if (k is < 1 or > 1000) throw new InvalidOperationException("Invalid development RRF configuration.");
        services.AddScoped<IDevTourCatalog, SqlDevTourCatalog>();
        services.AddHttpClient("tm70-dev-embedding", c => { c.BaseAddress = embedding; c.Timeout = TimeSpan.FromSeconds(15); })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient("tm70-dev-qdrant", c => { c.BaseAddress = qdrant; c.Timeout = TimeSpan.FromSeconds(10); })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped(sp => new DevTourSearch(sp.GetRequiredService<IDevTourCatalog>(),
            new HttpEmbeddingService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("tm70-dev-embedding")),
            new QdrantTourVectorSearch(sp.GetRequiredService<IHttpClientFactory>().CreateClient("tm70-dev-qdrant"), collection),
            sp.GetRequiredService<IDateTimeProvider>(), k));
        return services;
    }
}