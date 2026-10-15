namespace TripMate.Application.Features.PointsOfInterest.Catalogue;

public sealed record PoiCatalogueDto(
    IReadOnlyList<PoiCatalogueOptionDto> Categories,
    IReadOnlyList<PoiCatalogueOptionDto> Tags);

public sealed record PoiCatalogueOptionDto(
    int Id,
    string Name);
