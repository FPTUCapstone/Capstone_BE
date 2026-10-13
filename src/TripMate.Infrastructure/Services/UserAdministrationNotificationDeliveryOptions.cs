using System.ComponentModel.DataAnnotations;

namespace TripMate.Infrastructure.Services;

public sealed class UserAdministrationNotificationDeliveryOptions
{
    public const string SectionName = "UserAdministrationNotificationDelivery";

    [Range(typeof(TimeSpan), "00:00:05", "1.00:00:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    [Range(typeof(TimeSpan), "00:00:30", "1.00:00:00")]
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    [Range(1, 100)]
    public int BatchSize { get; init; } = 20;
}