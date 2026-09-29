using System.Text.Json.Serialization;

using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Common.Serialization;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Create;

public sealed record CreatePoiCommand(
    string Name,
    int CategoryId,
    decimal? Latitude,
    decimal? Longitude,
    string? Address = null,
    string? Description = null,
    [property: JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
    IndoorOutdoorType? IndoorOutdoor = null,
    int? AverageVisitDurationMinutes = null,
    bool? HasShelter = null,
    IReadOnlyCollection<CreatePoiOpeningHourInput>? OpeningHours = null,
    IReadOnlyCollection<int>? TagIds = null,
    bool ConfirmDuplicate = false)
    : IRequest<Result<PoiResponseDto>>;