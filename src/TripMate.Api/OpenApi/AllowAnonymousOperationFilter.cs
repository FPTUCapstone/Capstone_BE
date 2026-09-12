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

            switch (mutableParam.Name)
            {
                case "search":
                    schema.MaxLength = ExplorePoisQuery.SearchMaxLength;
                    break;

                case "categoryId":
                    schema.Minimum = "1";
                    break;

                case "originLatitude":
                    schema.Minimum = "-90";
                    schema.Maximum = "90";
                    break;

                case "originLongitude":
                    schema.Minimum = "-180";
                    schema.Maximum = "180";
                    break;

                case "maxDistanceKm":
                    schema.Minimum = "0";
                    break;

                case "openNow":
                    schema.Default = JsonValue.Create(false);
                    break;

                case "sort":
                    schema.Default = JsonValue.Create(ExplorePoisQuery.DefaultSort);
                    schema.Enum = new List<JsonNode>
                    {
                        JsonValue.Create("name")!,
                        JsonValue.Create("distance")!,
                        JsonValue.Create("rating")!,
                    };
                    break;

                case "page":
                    schema.Default = JsonValue.Create(ExplorePoisQuery.DefaultPage);
                    schema.Minimum = "1";
                    break;

                case "pageSize":
                    schema.Default = JsonValue.Create(ExplorePoisQuery.DefaultPageSize);
                    schema.Minimum = "1";
                    schema.Maximum = ExplorePoisQuery.MaxPageSize.ToString();
                    break;
            }
        }
    }
}