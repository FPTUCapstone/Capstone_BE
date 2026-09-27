namespace TripMate.Application.Features.TourMedia.Common;

/// <summary>
/// Public-safe projection of Tour-owned media. Storage provider identifiers are excluded.
/// </summary>
public sealed record TourMediaDto(
    long TourMediaId,
    long TourId,
    string DeliveryUrl,
    string? Caption,
    string AltText,
    int SortOrder,
    bool IsPrimary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);