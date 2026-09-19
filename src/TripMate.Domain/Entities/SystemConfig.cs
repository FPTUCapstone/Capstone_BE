namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.SystemConfigs in database/tripmate_schema_v7.sql — platform configuration
/// key/value rows. Algorithm parameter rows (UC-57) are managed through this entity;
/// foreign config rows belong to their owning use cases.
/// </summary>
public class SystemConfig
{
    private SystemConfig()
    {
    }

    public SystemConfig(
        string configKey,
        string configValue,
        string? description,
        long? updatedBy,
        DateTimeOffset updatedAtUtc)
    {
        Validate(configKey, configValue, description);

        ConfigKey = configKey;
        ConfigValue = configValue;
        Description = description;
        UpdatedBy = updatedBy;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string ConfigKey { get; private set; } = string.Empty;

    public string ConfigValue { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public long? UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void UpdateValue(string configValue, long? updatedBy, DateTimeOffset updatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configValue);
        if (configValue.Length > 500)
        {
            throw new ArgumentException("Config value exceeds its database column length.", nameof(configValue));
        }

        ConfigValue = configValue;
        UpdatedBy = updatedBy;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static void Validate(string configKey, string configValue, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(configValue);
        if (configKey.Length > 100 || configValue.Length > 500 || description?.Length > 500)
        {
            throw new ArgumentException("System config exceeds its database column length.");
        }
    }
}