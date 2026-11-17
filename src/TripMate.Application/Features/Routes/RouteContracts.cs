using System.Text.Json.Serialization;

using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Routes;

public static class RouteErrors
{
    public const string Msg01 = "MSG01";
    public const string Msg126 = "MSG126";
    public const string Msg127 = "MSG127";
    public const string Msg139 = "MSG139";
    public const string Msg141 = "MSG141";
    public const string Msg143 = "MSG143";
    public const string InvalidInput = "Routes.InvalidInput";
    public const string DuplicateSubmission = "Routes.DuplicateSubmission";
    public const string DuplicateRoute = "Routes.DuplicateRoute";
    public const string Required = "This field is required.";
    public const string Forbidden = "You do not have permission to access this function.";
    public const string Unavailable = "TripMate is temporarily unable to process your request. Please check your connection and try again.";
    public const string Created = "Route created successfully.";
    public const string InvalidEndpoints = "Select two different Active points of interest.";
    public const string InvalidNumbers = "Distance and estimated travel time must be positive values.";
}

public sealed record CreateRouteCommand : IRequest<Result<RouteDto>>
{
    public long? FromPoiId { get; init; }
    public long? ToPoiId { get; init; }
    public decimal? DistanceKm { get; init; }
    public int? EstimatedDurationMinutes { get; init; }
    public string? TransportMode { get; init; }
    public string? RouteNote { get; init; }
    [JsonIgnore] public Guid IdempotencyKey { get; init; }
}

public sealed record RouteDto(long Id, long FromPoiId, long ToPoiId, string FromPoiName,
    string ToPoiName, decimal DistanceKm, int EstimatedDurationMinutes, string TransportMode,
    string Status, string? RouteNote, DateTimeOffset CreatedAtUtc,
    string? MessageCode = null, string? Message = null)
{
    public static RouteDto From(RouteSegment route) => new(route.Id, route.FromPoiId,
        route.ToPoiId, route.FromPoi.Name, route.ToPoi.Name, route.DistanceKm,
        route.EstimatedDurationMinutes, route.TransportMode.ToString(), route.Status.ToString(),
        route.RouteNote, route.CreatedAtUtc);
}

public sealed record ListRoutesQuery(int Page = 1, int PageSize = 20) : IRequest<Result<RouteListDto>>;
public sealed record RouteListDto(IReadOnlyList<RouteDto> Items, int Page, int PageSize, int TotalCount);
public sealed record RouteOptionsQuery : IRequest<Result<RouteOptionsDto>>;
public sealed record RoutePoiOption(long Id, string Name, decimal Latitude, decimal Longitude);
public sealed record RouteOptionsDto(IReadOnlyList<RoutePoiOption> Pois, IReadOnlyList<string> TransportModes);