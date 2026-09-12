using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

using TripMate.Api.Common;

namespace TripMate.Api.OpenApi;

public sealed class ProblemDetailsContractSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema mutableSchema)
        {
            return;
        }

        if (context.Type == typeof(ValidationProblemDetails))
        {
            RequireNonNullableProperty(mutableSchema, "errors");
            return;
        }

        if (context.Type == typeof(ErrorCodeProblemDetails))
        {
            RequireNonNullableProperty(mutableSchema, "errorCode");
            return;
        }

        if (context.Type == typeof(PossibleDuplicateProblemDetails))
        {
            RequireNonNullableProperty(mutableSchema, "errorCode");
            RequireNonNullableProperty(mutableSchema, "existingPoiId");
        }
    }

    private static void RequireNonNullableProperty(OpenApiSchema schema, string name)
    {
        var properties = schema.Properties
            ?? throw new InvalidOperationException("ProblemDetails OpenAPI properties were not generated.");
        if (!properties.TryGetValue(name, out var property)
            || property is not OpenApiSchema mutableProperty)
        {
            throw new InvalidOperationException(
                $"ProblemDetails OpenAPI property '{name}' was not generated.");
        }

        schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
        schema.Required.Add(name);

        if (mutableProperty.Type.HasValue)
        {
            mutableProperty.Type &= ~JsonSchemaType.Null;
        }
    }
}