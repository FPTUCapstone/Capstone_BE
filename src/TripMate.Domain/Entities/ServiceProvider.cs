using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class ServiceProvider : BaseEntity
{
    public const int NameMaxLength = 150;
    public const int ContactEmailMaxLength = 200;
    public const int ContactPhoneMaxLength = 20;
    public const string StatusActive = "Active";
    public const string StatusInactive = "Inactive";

    private ServiceProvider()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string ServiceCategory { get; private set; } = string.Empty;

    public string? ContactEmail { get; private set; }

    public string? ContactPhone { get; private set; }

    public string Status { get; private set; } = StatusActive;

    public static ServiceProvider Create(
        string name,
        string serviceCategory,
        string status,
        string? contactEmail = null,
        string? contactPhone = null)
    {
        if (!CommercialService.AllowedCategories.Contains(serviceCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(serviceCategory));
        }

        if (status is not (StatusActive or StatusInactive))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new ServiceProvider
        {
            Name = NormalizeRequired(name, NameMaxLength, nameof(name)),
            ServiceCategory = serviceCategory,
            Status = status,
            ContactEmail = NormalizeOptional(contactEmail, ContactEmailMaxLength, nameof(contactEmail)),
            ContactPhone = NormalizeOptional(contactPhone, ContactPhoneMaxLength, nameof(contactPhone)),
        };
    }

    private static string NormalizeRequired(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return normalized;
    }
}