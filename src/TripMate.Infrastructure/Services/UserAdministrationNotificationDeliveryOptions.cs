using System.ComponentModel.DataAnnotations;

namespace TripMate.Infrastructure.Services;

public sealed class UserAdministrationNotificationDeliveryOptions
{
    public const string SectionName = "UserAdministrationNotificationDelivery";

    [Range(typeof(TimeSpan), "00:00:05", "1.00:00:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    // SMTP providers may take several minutes to accept a message. A conservative lease
    // prevents a second worker from reclaiming the same in-flight account-unlock email.
    [Range(typeof(TimeSpan), "00:05:00", "1.00:00:00")]
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(15);

    [Range(1, 100)]
    public int BatchSize { get; init; } = 20;
}