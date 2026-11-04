namespace TripMate.Domain.Entities;

public sealed class WeatherSafetyReview
{
    public const string StatusPendingAcknowledgement = "PendingAcknowledgement";
    public const string StatusAcknowledged = "Acknowledged";
    public const string StatusEscalated = "Escalated";
    public const string StatusClosed = "Closed";

    public const int ProviderMaxLength = 50;
    public const int ProviderEventIdMaxLength = 150;
    public const int StatusMaxLength = 30;
    public const int EscalationContactMaxLength = 200;
    public const int InstructionsMaxLength = 1000;

    private WeatherSafetyReview() { }

    public long Id { get; private set; }
    public long TourScheduleId { get; private set; }
    public TourSchedule TourSchedule { get; private set; } = null!;
    public string Provider { get; private set; } = string.Empty;
    public string ProviderEventId { get; private set; } = string.Empty;
    public string Status { get; private set; } = StatusPendingAcknowledgement;
    public DateTimeOffset UrgentNoticeSentAtUtc { get; private set; }
    public DateTimeOffset AcknowledgementDeadlineUtc { get; private set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }
    public long? AcknowledgedByOperatorId { get; private set; }
    public DateTimeOffset? EscalatedAtUtc { get; private set; }
    public string? EscalationContact { get; private set; }
    public string? ImmediateInstructions { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static WeatherSafetyReview CreateHold(
        long tourScheduleId,
        string provider,
        string providerEventId,
        DateTimeOffset urgentNoticeSentAtUtc,
        DateTimeOffset acknowledgementDeadlineUtc,
        string? immediateInstructions = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tourScheduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerEventId);

        return new WeatherSafetyReview
        {
            TourScheduleId = tourScheduleId,
            Provider = provider.Trim(),
            ProviderEventId = providerEventId.Trim(),
            Status = StatusPendingAcknowledgement,
            UrgentNoticeSentAtUtc = urgentNoticeSentAtUtc.ToUniversalTime(),
            AcknowledgementDeadlineUtc = acknowledgementDeadlineUtc.ToUniversalTime(),
            ImmediateInstructions = string.IsNullOrWhiteSpace(immediateInstructions) ? null : immediateInstructions.Trim(),
            CreatedAtUtc = urgentNoticeSentAtUtc.ToUniversalTime(),
        };
    }

    public bool Acknowledge(long operatorId, DateTimeOffset acknowledgedAtUtc, string? instructions = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operatorId);
        if (Status is StatusClosed) return false;

        AcknowledgedByOperatorId = operatorId;
        AcknowledgedAtUtc = acknowledgedAtUtc.ToUniversalTime();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            ImmediateInstructions = instructions.Trim();
        }
        Status = StatusAcknowledged;
        return true;
    }

    public bool Escalate(DateTimeOffset escalatedAtUtc, string escalationContact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(escalationContact);
        if (Status is StatusAcknowledged or StatusClosed or StatusEscalated) return false;

        EscalatedAtUtc = escalatedAtUtc.ToUniversalTime();
        EscalationContact = escalationContact.Trim();
        Status = StatusEscalated;
        return true;
    }

    public void Close()
    {
        Status = StatusClosed;
    }
}
