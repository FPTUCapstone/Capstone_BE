using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

using TripMate.Application.Features.PointsOfInterest.Explore;

namespace TripMate.Api.OpenApi;

public sealed class AllowAnonymousOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var isAllowAnonymous = context.MethodInfo.DeclaringType?
            .GetCustomAttributes(true)
            .OfType<AllowAnonymousAttribute>()
            .Any() == true
            || context.MethodInfo
                .GetCustomAttributes(true)
                .OfType<AllowAnonymousAttribute>()
                .Any();

        if (isAllowAnonymous)
        {
            operation.Security = new List<OpenApiSecurityRequirement>();
        }

        if (context.MethodInfo.Name == nameof(Controllers.V1.PublicPointsOfInterestController.Explore)
            && operation.Parameters is not null)
        {
            ConfigureExploreParameters(operation.Parameters);
        }
    }

    private static void ConfigureExploreParameters(IList<IOpenApiParameter> parameters)
    {
        foreach (var param in parameters)
        {
            if (param is not OpenApiParameter mutableParam || string.IsNullOrEmpty(mutableParam.Name) || mutableParam.Schema is not OpenApiSchema schema)
            {
                continue;
            }

            mutableParam.Name = char.ToLowerInvariant(mutableParam.Name[0]) + mutableParam.Name[1..];

            if (string.Equals(mutableParam.Name, nameof(ExplorePoisQuery.Search), StringComparison.OrdinalIgnoreCase))
            {
                schema.MaxLength = ExplorePoisQuery.SearchMaxLength;
            }
            else if (string.Equals(mutableParam.Name, nameof(ExplorePoisQuery.CategoryId), StringComparison.OrdinalIgnoreCase))
            {
                schema.Minimum = ExplorePoisQuery.MinCategoryId.ToString();
            }
            else if (string.Equals(mutableParam.Name, nameof(ExplorePoisQuery.OriginLatitude), StringComparison.OrdinalIgnoreCase))
            {
                schema.Minimum = ExplorePoisQuery.MinLatitude.ToString();
                schema.Maximum = ExplorePoisQuery.MaxLatitude.ToString();
            }
            else if (string.Equals(mutableParam.Name, nameof(ExplorePoisQuery.OriginLongitude), StringComparison.OrdinalIgnoreCase))
            {
                schema.Minimum = ExplorePoisQuery.MinLongitude.ToString();
                schema.Maximum = ExplorePoisQuery.MaxLongitude.ToString();
            }
            else if (string.Equals(mutableParam.Name, nameof(ExplorePoisQuery.MaxDistanceKm), StringComparison.OrdinalIgnoreCase))
            {
                schema.ExclusiveMinimum = ExplorePoisQuery.MinDistanceKm.ToString();
            }
            else if (string.Equals(mutableParam.Name, nameof(ExplorePoisQuery.OpenNow), StringComparison.OrdinalIgnoreCase))
            {
                schema.Default = JsonValue.Create(false);
            }
            else if (string.Equals(mutableParam.Name, nameof(ExplorePoisQuery.Sort), StringComparison.OrdinalIgnoreCase))
            {
                schema.Default = JsonValue.Create(ExplorePoisQuery.DefaultSort);
                schema.Enum = ExplorePoisQuery.AllowedSorts
                    .Select(s => (JsonNode)JsonValue.Create(s)!)
                    .ToList();
            }
            else if (string.Equals(mutableParam.Name, nameof(ExplorePoisQuery.Page), StringComparison.OrdinalIgnoreCase))
            {
                schema.Default = JsonValue.Create(ExplorePoisQuery.DefaultPage);
                schema.Minimum = ExplorePoisQuery.MinPage.ToString();
            }
            else if (string.Equals(mutableParam.Name, nameof(ExplorePoisQuery.PageSize), StringComparison.OrdinalIgnoreCase))
            {
                schema.Default = JsonValue.Create(ExplorePoisQuery.DefaultPageSize);
                schema.Minimum = ExplorePoisQuery.MinPageSize.ToString();
                schema.Maximum = ExplorePoisQuery.MaxPageSize.ToString();
            }
        }
    }
}