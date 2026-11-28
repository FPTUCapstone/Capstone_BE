using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

namespace TripMate.Api.OpenApi;

/// <summary>
/// Adds an explicit operation-level Bearer security requirement for actions annotated
/// with <see cref="IncludeOperationSecurityAttribute"/>. Required when an OpenAPI test
/// asserts security at the operation level rather than relying on the global security.
/// </summary>
public sealed class ExplicitSecurityOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!context.MethodInfo.GetCustomAttributes(typeof(IncludeOperationSecurityAttribute), inherit: false).Any())
            return;

        operation.Security ??= new List<OpenApiSecurityRequirement>();
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecuritySchemeReference("Bearer"),
                new List<string>()
            },
        });
    }
}