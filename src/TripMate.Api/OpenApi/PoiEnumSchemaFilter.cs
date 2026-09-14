using System.Text.Json.Nodes;

using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

using TripMate.Domain.Enums;

namespace TripMate.Api.OpenApi;

public sealed class PoiEnumSchemaFilter : ISchemaFilter
{
    private static readonly HashSet<Type> PoiEnumTypes =
    [
        typeof(IndoorOutdoorType),
        typeof(PointOfInterestStatus),
    ];

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        var enumType = Nullable.GetUnderlyingType(context.Type) ?? context.Type;
        if (!PoiEnumTypes.Contains(enumType) || schema is not OpenApiSchema mutableSchema)
        {
            return;
        }

        mutableSchema.Type = JsonSchemaType.String;
        mutableSchema.Format = null;
        mutableSchema.Enum = Enum.GetNames(enumType)
            .Select(name => JsonValue.Create(name)!)
            .Cast<JsonNode>()
            .ToList();
    }
}