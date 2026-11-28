using System.Text.Json;

using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

namespace TripMate.Api.OpenApi;

public sealed class FormPropertyCamelCaseOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.RequestBody?.Content is null) return;
        foreach (var mediaType in operation.RequestBody.Content.Values)
        {
            var schema = mediaType.Schema;
            if (schema?.Properties is null || schema.Properties.Count == 0) continue;
            var renames = schema.Properties
                .Where(kvp => kvp.Key != JsonNamingPolicy.CamelCase.ConvertName(kvp.Key))
                .Select(kvp => (Old: kvp.Key, New: JsonNamingPolicy.CamelCase.ConvertName(kvp.Key), Schema: kvp.Value))
                .ToList();
            foreach (var (old, @new, propSchema) in renames)
            {
                schema.Properties.Remove(old);
                schema.Properties[@new] = propSchema;
            }
        }
    }
}