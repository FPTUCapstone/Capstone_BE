namespace TripMate.Application.Features.Admin.SystemConfigs.GetAlgorithmParameters;

public record AlgorithmParametersDto(
    int BufferTimeMinutes,
    double DefaultTravelSpeedKmh,
    double ReroutingSearchRadiusKm,
    string WeatherAlertThresholdSeverity,
    DateTimeOffset? UpdatedAtUtc,
    string? UpdatedAtLocal);