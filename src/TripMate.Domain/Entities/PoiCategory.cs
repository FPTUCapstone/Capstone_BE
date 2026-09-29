namespace TripMate.Domain.Entities;

public class PoiCategory
{
    private PoiCategory()
    {
    }

    public int Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public static PoiCategory Create(string name, string? description)
    {
        var normalizedName = NormalizeRequired(name, 100, nameof(name));
        var normalizedDescription = NormalizeOptional(description, 300, nameof(description));

        return new PoiCategory
        {
            Name = normalizedName,
            Description = normalizedDescription,
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