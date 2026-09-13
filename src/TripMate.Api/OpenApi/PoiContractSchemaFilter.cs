using System.Text.Json.Nodes;

using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.PointsOfInterest.Create;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.OpenApi;

public sealed class PoiContractSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema mutableSchema)
        {
            return;
        }

        if (context.Type == typeof(CreatePoiCommand))
        {
            ConfigureCreateRequest(mutableSchema);
            return;
        }

        if (context.Type == typeof(CreatePoiOpeningHourInput))
        {
            ConfigureOpeningHoursInput(mutableSchema);
            return;
        }

        if (context.Type == typeof(PoiResponseDto))
        {
            ConfigureResponse(mutableSchema);
            return;
        }

        if (context.Type == typeof(PoiOpeningHourDto))
        {
            RequireEveryProperty(mutableSchema);
        }
    }

    private static void ConfigureCreateRequest(OpenApiSchema schema)
    {
        schema.Required = new HashSet<string>
        {
            "name",
            "categoryId",
            "latitude",
            "longitude",
        };

        var name = Property(schema, "name");
        name.MinLength = 1;
        name.MaxLength = PointOfInterest.NameMaxLength;
        RemoveNullType(name);

        Property(schema, "categoryId").Minimum = "1";

        var latitude = Property(schema, "latitude");
        latitude.Minimum = "-90";
        latitude.Maximum = "90";
        RemoveNullType(latitude);

        var longitude = Property(schema, "longitude");
        longitude.Minimum = "-180";
        longitude.Maximum = "180";
        RemoveNullType(longitude);

        Property(schema, "address").MaxLength = PointOfInterest.AddressMaxLength;
        Property(schema, "description").MaxLength = PointOfInterest.DescriptionMaxLength;

        var averageDuration = Property(schema, "averageVisitDurationMinutes");
        averageDuration.Minimum = "1";
        averageDuration.Default = JsonValue.Create(
            PointOfInterest.DefaultAverageVisitDurationMinutes);

        Properties(schema)["indoorOutdoor"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
            Enum = Enum.GetNames<IndoorOutdoorType>()
                .Select(name => JsonValue.Create(name)!)
                .Cast<JsonNode>()
                .Append(null!)
                .ToList(),
            Default = JsonValue.Create("Outdoor"),
        };

        Property(schema, "hasShelter").Default = JsonValue.Create(false);
        Property(schema, "confirmDuplicate").Default = JsonValue.Create(false);
        Property(schema, "openingHours").MaxItems = 7;

        var tagIds = Property(schema, "tagIds");
        tagIds.UniqueItems = true;
        if (tagIds.Items is OpenApiSchema tagId)
        {
            tagId.Minimum = "1";
        }
    }

    private static void ConfigureOpeningHoursInput(OpenApiSchema schema)
    {
        schema.Required = new HashSet<string> { "dayOfWeek" };
        var dayOfWeek = Property(schema, "dayOfWeek");
        dayOfWeek.Minimum = "0";
        dayOfWeek.Maximum = "6";
        RemoveNullType(dayOfWeek);
    }

    private static void ConfigureResponse(OpenApiSchema schema)
    {
        RequireEveryProperty(schema);
        RemoveNullType(Property(schema, "name"));
        RemoveNullType(Property(schema, "createdById"));
        RemoveNullType(Property(schema, "openingHours"));
        RemoveNullType(Property(schema, "tagIds"));
    }

    private static void RequireEveryProperty(OpenApiSchema schema) =>
        schema.Required = Properties(schema).Keys.ToHashSet(StringComparer.Ordinal);

    private static OpenApiSchema Property(OpenApiSchema schema, string name) =>
        Properties(schema).TryGetValue(name, out var property)
        && property is OpenApiSchema mutableProperty
            ? mutableProperty
            : throw new InvalidOperationException($"POI OpenAPI property '{name}' was not generated.");

    private static IDictionary<string, IOpenApiSchema> Properties(OpenApiSchema schema) =>
        schema.Properties
        ?? throw new InvalidOperationException("POI OpenAPI properties were not generated.");

    private static void RemoveNullType(OpenApiSchema schema)
    {
        if (schema.Type.HasValue)
        {
            schema.Type &= ~JsonSchemaType.Null;
        }
    }
}