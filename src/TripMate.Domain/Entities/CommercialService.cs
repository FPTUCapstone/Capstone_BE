using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class CommercialService : BaseEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2_000;
    public const int AttributesJsonMaxLength = 1_000;
    public const int CurrencyCodeLength = 3;
    public const int CoverImageUrlMaxLength = 500;
    public const int FulfilmentLocationLabelMaxLength = 300;
    public const int PickupOrArrivalInstructionsMaxLength = 1_000;
    public const int CancellationPolicySummaryMaxLength = 500;

    public const string CategoryVehicle = "Vehicle";
    public const string CategoryHotel = "Hotel";
    public const string CategoryRestaurant = "Restaurant";
    public static readonly string[] AllowedCategories =
    [
        CategoryVehicle,
        CategoryHotel,
        CategoryRestaurant
    ];

    public const string AvailabilityAvailable = "Available";
    public const string AvailabilityUnavailable = "Unavailable";
    public const string PriceUnitPerDay = "PerDay";
    public const string PriceUnitPerNight = "PerNight";
    public const string PriceUnitPerItem = "PerItem";
    public const string PriceUnitPerPerson = "PerPerson";
    public static readonly string[] AllowedPriceUnits =
    [
        PriceUnitPerDay,
        PriceUnitPerNight,
        PriceUnitPerItem,
        PriceUnitPerPerson
    ];

    private CommercialService()
    {
    }

    public long ProviderId { get; private set; }

    public ServiceProvider Provider { get; private set; } = null!;

    public string ServiceCategory { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public long? PoiId { get; private set; }

    public decimal PriceAmount { get; private set; }

    public string PriceUnit { get; private set; } = string.Empty;

    public int? Capacity { get; private set; }

    public string? AttributesJson { get; private set; }

    public string AvailabilityStatus { get; private set; } = AvailabilityAvailable;

    public string CurrencyCode { get; private set; } = "VND";

    public bool PriceIncludesTax { get; private set; }

    public decimal? RefundableDepositAmount { get; private set; }

    public string? CoverImageUrl { get; private set; }

    public string? FulfilmentLocationLabel { get; private set; }

    public string? PickupOrArrivalInstructions { get; private set; }

    public string? CancellationPolicySummary { get; private set; }

    public DateTimeOffset LastUpdatedAtUtc { get; private set; }

    public static CommercialService Create(
        ServiceProvider provider,
        string serviceCategory,
        string name,
        decimal priceAmount,
        string priceUnit,
        string availabilityStatus,
        string currencyCode,
        bool priceIncludesTax,
        decimal? refundableDepositAmount,
        string? coverImageUrl,
        string? attributesJson,
        DateTimeOffset lastUpdatedAtUtc,
        string? description = null,
        long? poiId = null,
        int? capacity = null,
        string? fulfilmentLocationLabel = null,
        string? pickupOrArrivalInstructions = null,
        string? cancellationPolicySummary = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (!AllowedCategories.Contains(serviceCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(serviceCategory));
        }

        if (!string.Equals(provider.ServiceCategory, serviceCategory, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Service category must match the provider category.",
                nameof(serviceCategory));
        }

        if (priceAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(priceAmount));
        }

        if (!AllowedPriceUnits.Contains(priceUnit))
        {
            throw new ArgumentOutOfRangeException(nameof(priceUnit));
        }

        if (availabilityStatus is not (AvailabilityAvailable or AvailabilityUnavailable))
        {
            throw new ArgumentOutOfRangeException(nameof(availabilityStatus));
        }

        if (!string.Equals(currencyCode, "VND", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(currencyCode));
        }

        if (refundableDepositAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(refundableDepositAmount));
        }

        if (poiId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(poiId));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        return new CommercialService
        {
            Provider = provider,
            ServiceCategory = serviceCategory,
            Name = NormalizeRequired(name, NameMaxLength, nameof(name)),
            Description = NormalizeOptional(description, DescriptionMaxLength, nameof(description)),
            PoiId = poiId,
            PriceAmount = priceAmount,
            PriceUnit = priceUnit,
            Capacity = capacity,
            AttributesJson = NormalizeOptional(
                attributesJson,
                AttributesJsonMaxLength,
                nameof(attributesJson)),
            AvailabilityStatus = availabilityStatus,
            CurrencyCode = currencyCode,
            PriceIncludesTax = priceIncludesTax,
            RefundableDepositAmount = refundableDepositAmount,
            CoverImageUrl = NormalizeHttpsUrl(coverImageUrl, nameof(coverImageUrl)),
            FulfilmentLocationLabel = NormalizeOptional(
                fulfilmentLocationLabel,
                FulfilmentLocationLabelMaxLength,
                nameof(fulfilmentLocationLabel)),
            PickupOrArrivalInstructions = NormalizeOptional(
                pickupOrArrivalInstructions,
                PickupOrArrivalInstructionsMaxLength,
                nameof(pickupOrArrivalInstructions)),
            CancellationPolicySummary = NormalizeOptional(
                cancellationPolicySummary,
                CancellationPolicySummaryMaxLength,
                nameof(cancellationPolicySummary)),
            LastUpdatedAtUtc = lastUpdatedAtUtc.ToUniversalTime(),
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

    private static string? NormalizeOptional(string? value, int? maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (maxLength.HasValue && normalized.Length > maxLength.Value)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength.Value} characters.", parameterName);
        }

        return normalized;
    }

    private static string? NormalizeHttpsUrl(string? value, string parameterName)
    {
        var normalized = NormalizeOptional(value, CoverImageUrlMaxLength, parameterName);
        if (normalized is null)
        {
            return null;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Value must be an absolute HTTPS URL.", parameterName);
        }

        return normalized;
    }
}