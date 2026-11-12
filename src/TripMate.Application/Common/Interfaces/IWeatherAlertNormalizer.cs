using TripMate.Application.Common.Models;
using TripMate.Application.Features.Weather.Emergency;

namespace TripMate.Application.Common.Interfaces;

public interface IWeatherAlertNormalizer
{
    bool CanHandle(string provider);

    Result<IReadOnlyList<NormalizedWeatherEvent>> Normalize(
        string rawPayload,
        string provider,
        string? providerNotificationId,
        DateTimeOffset receivedAtUtc);
}