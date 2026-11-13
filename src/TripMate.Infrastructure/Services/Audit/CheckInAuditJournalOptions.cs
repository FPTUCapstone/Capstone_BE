namespace TripMate.Infrastructure.Services.Audit;

public sealed class CheckInAuditJournalOptions
{
    public const string SectionName = "CheckInAuditJournal";

    public bool Enabled { get; set; }

    public string RedisConnectionStringName { get; set; } = "Redis";

    public string AdmissionStreamKey { get; set; } = "tripmate:checkin:audit:admissions";

    public string RejectionStreamKey { get; set; } = "tripmate:checkin:audit:rejections";

    public string ReceiptKeyPrefix { get; set; } = "tripmate:checkin:audit:receipts";

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan DurabilityTimeout { get; set; } = TimeSpan.FromSeconds(1);

    public int MaxAppendAttempts { get; set; } = 2;

    public string ConsumerGroup { get; set; } = "tripmate-checkin-audit-projectors";

    public string ConsumerName { get; set; } = $"backend-{Environment.MachineName}";

    public int ProjectionBatchSize { get; set; } = 20;

    public TimeSpan PendingLease { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}