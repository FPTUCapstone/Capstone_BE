using System.Text.Json.Serialization;

namespace TripMate.Api.Controllers.V1.Requests;

public sealed record GetPoiRecommendationsApiRequest(
    [property: JsonRequired] decimal ExplorationLatitude,
    [property: JsonRequired] decimal ExplorationLongitude,
    [property: JsonRequired] int SearchRadiusKm,
    int? Limit);
