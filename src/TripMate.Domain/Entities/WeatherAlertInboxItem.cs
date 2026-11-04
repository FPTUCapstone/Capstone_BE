namespace TripMate.Domain.Entities;

public sealed class WeatherAlertInboxItem
{
    public const string StatusPending = "Pending";
    public const string StatusProcessing = "Processing";
    public const string StatusProcessed = "Processed";
    public const string StatusFailed = "Failed";
    public const string StatusDiscarded = "Discarded";

    public const int ProviderMaxLength = 50;
    public const int ProviderNotificationIdMaxLength = 150;
    public const int SignatureMaxLength = 200;
    public const int StatusMaxLength = 30;
    public const int LeasedByMaxLength = 100;
    public const int LastErrorMaxLength = 1000;

    private WeatherAlertInboxItem() { }

    public long Id { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string? ProviderNotificationId { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public string? Signature { get; private set; }
    public string Status { get; private set; } = StatusPending;
    public int AttemptCount { get; private set; }
    public int MaxAttempts { get; private set; } = 3;
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }
    public string? LeasedBy { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset ReceivedAtUtc { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public static WeatherAlertInboxItem CreatePending(
        string provider,
        string payload,
        string? providerNotificationId,
        string? signature,
        DateTimeOffset receivedAtUtc,
        int maxAttempts = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new WeatherAlertInboxItem
        {
            Provider = provider.Trim(),
            Payload = payload,
            ProviderNotificationId = string.IsNullOrWhiteSpace(providerNotificationId) ? null : providerNotificationId.Trim(),
            Signature = string.IsNullOrWhiteSpace(signature) ? null : signature.Trim(),
            Status = StatusPending,
            AttemptCount = 0,
            MaxAttempts = maxAttempts,
            ReceivedAtUtc = receivedAtUtc.ToUniversalTime(),
        };
    }

    public bool AcquireLease(string workerId, DateTimeOffset leaseExpiryUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        LeasedBy = workerId;
        LeaseExpiresAtUtc = leaseExpiryUtc.ToUniversalTime();
        Status = StatusProcessing;
        AttemptCount++;
        return true;
    }

    public void MarkProcessed(DateTimeOffset processedAtUtc)
    {
        Status = StatusProcessed;
        ProcessedAtUtc = processedAtUtc.ToUniversalTime();
        LeaseExpiresAtUtc = null;
        LeasedBy = null;
    }

    public void MarkFailed(string error, DateTimeOffset? nextRetryLeaseExpiryUtc = null)
    {
        LastError = error.Length > LastErrorMaxLength ? error[..LastErrorMaxLength] : error;
        if (AttemptCount >= MaxAttempts)
        {
            Status = StatusFailed;
            LeaseExpiresAtUtc = null;
            LeasedBy = null;
        }
        else
        {
            Status = StatusPending;
            LeaseExpiresAtUtc = nextRetryLeaseExpiryUtc?.ToUniversalTime();
            LeasedBy = null;
        }
    }

    public void MarkDiscarded(string reason)
    {
        Status = StatusDiscarded;
        LastError = reason.Length > LastErrorMaxLength ? reason[..LastErrorMaxLength] : reason;
        LeaseExpiresAtUtc = null;
        LeasedBy = null;
    }
}
