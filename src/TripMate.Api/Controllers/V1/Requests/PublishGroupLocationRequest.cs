namespace TripMate.Api.Controllers.V1.Requests;

public sealed record PublishGroupLocationRequest(decimal? Latitude, decimal? Longitude, string? SessionVersion);