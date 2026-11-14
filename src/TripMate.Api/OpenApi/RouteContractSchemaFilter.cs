using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

using TripMate.Application.Features.Routes;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.OpenApi;

public sealed class RouteContractSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(CreateRouteCommand) || schema is not OpenApiSchema mutable
            || mutable.Properties is null) return;
        var required = new[] { nameof(CreateRouteCommand.FromPoiId), nameof(CreateRouteCommand.ToPoiId),
            nameof(CreateRouteCommand.DistanceKm), nameof(CreateRouteCommand.EstimatedDurationMinutes),
            nameof(CreateRouteCommand.TransportMode) };
        mutable.Required = required.Select(JsonNamingPolicy.CamelCase.ConvertName).ToHashSet();
        foreach (var name in required)
        {
            if (Property(mutable, name) is { } property && property.Type.HasValue)
                property.Type &= ~JsonSchemaType.Null;
        }
        if (Property(mutable, nameof(CreateRouteCommand.FromPoiId)) is { } from) from.Minimum = "1";
        if (Property(mutable, nameof(CreateRouteCommand.ToPoiId)) is { } to) to.Minimum = "1";
        if (Property(mutable, nameof(CreateRouteCommand.DistanceKm)) is { } distance)
        {
            distance.Minimum = "0.01";
            distance.Maximum = RouteSegment.MaximumDistanceKm.ToString(CultureInfo.InvariantCulture);
            distance.MultipleOf = 0.01m;
        }
        if (Property(mutable, nameof(CreateRouteCommand.EstimatedDurationMinutes)) is { } duration) duration.Minimum = "1";
        if (Property(mutable, nameof(CreateRouteCommand.TransportMode)) is { } mode)
            mode.Enum = Enum.GetNames<TransportMode>().Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
        if (Property(mutable, nameof(CreateRouteCommand.RouteNote)) is { } note) note.MaxLength = RouteSegment.NoteMaxLength;
    }

    private static OpenApiSchema? Property(OpenApiSchema schema, string name) =>
        schema.Properties?.FirstOrDefault(entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase)).Value as OpenApiSchema;
}