using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.Services.Audit;

public sealed class CheckInAuditJournalOptionsValidator(IConfiguration? configuration = null)
    : IValidateOptions<CheckInAuditJournalOptions>
{
    public ValidateOptionsResult Validate(string? name, CheckInAuditJournalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        Required(options.RedisConnectionStringName, nameof(options.RedisConnectionStringName), failures);
        Required(options.AdmissionStreamKey, nameof(options.AdmissionStreamKey), failures);
        Required(options.RejectionStreamKey, nameof(options.RejectionStreamKey), failures);
        Required(options.ReceiptKeyPrefix, nameof(options.ReceiptKeyPrefix), failures);
        Required(options.ConsumerGroup, nameof(options.ConsumerGroup), failures);
        Required(options.ConsumerName, nameof(options.ConsumerName), failures);

        if (string.Equals(options.AdmissionStreamKey, options.RejectionStreamKey, StringComparison.Ordinal))
        {
            failures.Add($"{nameof(options.RejectionStreamKey)} must differ from {nameof(options.AdmissionStreamKey)}.");
        }

        if (options.CommandTimeout <= TimeSpan.Zero || options.CommandTimeout > TimeSpan.FromSeconds(30))
        {
            failures.Add($"{nameof(options.CommandTimeout)} must be greater than zero and at most 30 seconds.");
        }

        if (options.DurabilityTimeout <= TimeSpan.Zero || options.DurabilityTimeout > options.CommandTimeout)
        {
            failures.Add($"{nameof(options.DurabilityTimeout)} must be greater than zero and no longer than {nameof(options.CommandTimeout)}.");
        }

        if (options.MaxAppendAttempts is < 1 or > 5)
        {
            failures.Add($"{nameof(options.MaxAppendAttempts)} must be between 1 and 5.");
        }

        if (options.ProjectionBatchSize is < 1 or > 200)
        {
            failures.Add($"{nameof(options.ProjectionBatchSize)} must be between 1 and 200.");
        }

        if (options.PendingLease <= TimeSpan.Zero || options.PendingLease > TimeSpan.FromHours(1))
        {
            failures.Add($"{nameof(options.PendingLease)} must be greater than zero and at most one hour.");
        }

        if (options.PollInterval <= TimeSpan.Zero || options.PollInterval > TimeSpan.FromMinutes(1))
        {
            failures.Add($"{nameof(options.PollInterval)} must be greater than zero and at most one minute.");
        }

        if (options.Enabled && configuration is not null &&
            string.IsNullOrWhiteSpace(configuration.GetConnectionString(options.RedisConnectionStringName)))
        {
            failures.Add($"ConnectionStrings:{options.RedisConnectionStringName} is required when the journal is enabled.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Required(string value, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{propertyName} is required.");
        }
    }
}