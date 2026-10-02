namespace TripMate.Application.Features.Scheduling.Explanation;

public static class ExplanationProviderErrorCodes
{
    public const string Network = "itinerary_explanation.provider_network";

    public const string Quota = "itinerary_explanation.provider_quota";

    public const string ServerError = "itinerary_explanation.provider_server_error";

    public const string InvalidResponse = "itinerary_explanation.provider_invalid_response";

    public const string Timeout = "itinerary_explanation.provider_timeout";
}