namespace TripMate.Domain.Entities;

public sealed class WeatherEventEvaluation
{
    public const int ProviderMaxLength = 50;
    public const int ProviderEventIdMaxLength = 150;
    public const int DecisionOutcomeMaxLength = 30;
    public const int ReasonCodeMaxLength = 100;
    public const int ReasonDetailsMaxLength = 1000;

    private WeatherEventEvaluation() { }

    public long Id { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string ProviderEventId { get; private set; } = string.Empty;
    public long TourScheduleId { get; private set; }
    public TourSchedule TourSchedule { get; private set; } = null!;
    public string DecisionOutcome { get; private set; } = string.Empty;
    public string ReasonCode { get; private set; } = string.Empty;
    public string? ReasonDetails { get; private set; }
    public DateTimeOffset EvaluatedAtUtc { get; private set; }

    public static WeatherEventEvaluation Record(
        string provider,
        string providerEventId,
        long tourScheduleId,
        string decisionOutcome,
        string reasonCode,
        string? reasonDetails,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerEventId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tourScheduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionOutcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        return new WeatherEventEvaluation
        {
            Provider = provider.Trim(),
            ProviderEventId = providerEventId.Trim(),
            TourScheduleId = tourScheduleId,
            DecisionOutcome = decisionOutcome.Trim(),
            ReasonCode = reasonCode.Trim(),
            ReasonDetails = string.IsNullOrWhiteSpace(reasonDetails) ? null : (reasonDetails.Length > ReasonDetailsMaxLength ? reasonDetails[..ReasonDetailsMaxLength] : reasonDetails.Trim()),
            EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime(),
        };
    }

    public void Reevaluate(
        string decisionOutcome,
        string reasonCode,
        string? reasonDetails,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionOutcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        DecisionOutcome = decisionOutcome.Trim();
        ReasonCode = reasonCode.Trim();
        ReasonDetails = string.IsNullOrWhiteSpace(reasonDetails) ? null : reasonDetails.Trim();
        EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();
    }
}
