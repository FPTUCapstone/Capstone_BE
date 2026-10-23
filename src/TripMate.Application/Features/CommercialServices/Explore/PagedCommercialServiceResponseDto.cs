namespace TripMate.Application.Features.CommercialServices.Explore;

public sealed record PagedCommercialServiceResponseDto(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<CommercialServiceListItemDto> Items);

public sealed record CommercialServiceListItemDto(
    long Id,
    string Category,
    string Name,
    string ProviderName,
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
    DateTimeOffset LastUpdatedAtUtc);