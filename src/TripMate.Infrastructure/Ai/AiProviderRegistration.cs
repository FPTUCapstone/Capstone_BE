using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Infrastructure.AiExplanation;
using TripMate.Infrastructure.AiRanking;
using TripMate.Infrastructure.TripMateAi;

namespace TripMate.Infrastructure.Ai;

/// <summary>
/// Chọn cài đặt cho <see cref="IPoiRankingProvider"/> và <see cref="IItineraryExplanationProvider"/> theo
/// <c>AiRanking:Provider</c> / <c>AiExplanation:Provider</c>. Không cấu hình thì giữ Gemini như trước.
/// </summary>
public static class AiProviderRegistration
{
    public static IServiceCollection AddPoiRankingHttpProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AiProviderKind kind = ReadKind(configuration, PoiRankingProviderOptions.SectionName);
        if (kind == AiProviderKind.TripMateAi)
        {
            services.AddHttpClient<IPoiRankingProvider, TripMateAiPoiRankingProvider>();
        }
        else
        {
            services.AddHttpClient<IPoiRankingProvider, HttpPoiRankingProvider>();
        }

        return services;
    }

    public static IServiceCollection AddItineraryExplanationHttpProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AiProviderKind kind = ReadKind(configuration, ExplanationProviderOptions.SectionName);
        if (kind == AiProviderKind.TripMateAi)
        {
            services.AddHttpClient<IItineraryExplanationProvider, TripMateAiExplanationProvider>();
        }
        else
        {
            services.AddHttpClient<IItineraryExplanationProvider, HttpItineraryExplanationProvider>();
        }

        return services;
    }

    private static AiProviderKind ReadKind(IConfiguration configuration, string sectionName) =>
        configuration.GetSection(sectionName).GetValue<AiProviderKind?>("Provider") ?? AiProviderKind.Gemini;
}