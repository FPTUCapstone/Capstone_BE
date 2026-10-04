namespace TripMate.Application.Features.CommercialServices.Detail;

public sealed record CommercialServiceDetailDto(
    long Id,
    long ProviderId,
    string Category,
    string Name,
    string ProviderName,
    string? ProviderContactEmail,
    string? ProviderContactPhone,
    string? Description,
    decimal PriceAmount,
    string CurrencyCode,
    string PriceUnit,
    bool PriceIncludesTax,
    decimal? RefundableDepositAmount,
    int? Capacity,
    IReadOnlyDictionary<string, object?> Attributes,
    string? CoverImageUrl,
    long? PoiId,
    string? FulfilmentLocationLabel,
    string? PickupOrArrivalInstructions,
    string? CancellationPolicySummary,
    DateTimeOffset LastUpdatedAtUtc);