using System.Text.Json;

using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

namespace TripMate.Api.OpenApi;

public sealed class QueryParameterCamelCaseOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>()
                     .Where(parameter => parameter.In == ParameterLocation.Query)
                 ?? [])
        {
            if (parameter.Name is { } name)
            {
                parameter.Name = JsonNamingPolicy.CamelCase.ConvertName(name);
            }
        }
    }
}