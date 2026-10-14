namespace TripMate.Domain.Entities;

/// <summary>
/// Server-owned taxonomy entry used to classify Tours.
/// Production seed ownership remains with Product/BA.
/// </summary>
public sealed class TourCategory
{
    public const int CodeMaxLength = 50;
    public const int NameMaxLength = 100;
    public const string EnglishCollation = "Latin1_General_100_CI_AS";

    private readonly List<Tour> _tours = [];

    private TourCategory()
    {
    }

    public int Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public IReadOnlyCollection<Tour> Tours => _tours.AsReadOnly();

    public static TourCategory Create(string code, string name)
    {
        return new TourCategory
        {
            Code = NormalizeRequired(code, CodeMaxLength, nameof(code)),
            Name = NormalizeRequired(name, NameMaxLength, nameof(name)),
            IsActive = true,
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
            throw new ArgumentException(
                $"Value cannot exceed {maxLength} characters.",
                parameterName);
        }

        return normalized;
    }
}