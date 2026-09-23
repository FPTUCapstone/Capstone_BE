using System.Globalization;

using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

using TripMate.Api.Controllers.V1;
using TripMate.Application.Features.Tours.Search;

namespace TripMate.Api.OpenApi;

public sealed class TourSearchOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(ToursController))
        {
            return;
        }

        operation.Security = [];
        operation.Parameters =
        [
            Parameter("destination", JsonSchemaType.String,
                $"Literal text matching any operator-tagged tour region; trimmed/NFC, maximum {TourSearchCriteria.DestinationMaxLength} UTF-16 code units.",
                maxLength: TourSearchCriteria.DestinationMaxLength),
            Parameter("departureDate", JsonSchemaType.String,
                "Vietnam calendar date in yyyy-MM-dd format.", "date"),
            Parameter("minPrice", JsonSchemaType.Integer,
                "Inclusive minimum whole-VND price.", "int64", 0, TourSearchCriteria.MaximumWholeVndPrice),
            Parameter("maxPrice", JsonSchemaType.Integer,
                "Inclusive maximum whole-VND price.", "int64", 0, TourSearchCriteria.MaximumWholeVndPrice),
            Parameter("page", JsonSchemaType.Integer,
                "One-based page number. Defaults to 1.", "int32", 1),
            Parameter("pageSize", JsonSchemaType.Integer,
                $"Items per page. Defaults to 20; maximum {TourSearchCriteria.MaximumPageSize}.",
                "int32", 1, TourSearchCriteria.MaximumPageSize),
        ];
    }

    private static OpenApiParameter Parameter(
        string name,
        JsonSchemaType type,
        string description,
        string? format = null,
        decimal? minimum = null,
        decimal? maximum = null,
        int? maxLength = null) => new()
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Description = description,
            Schema = new OpenApiSchema
            {
                Type = type,
                Format = format,
                Minimum = minimum?.ToString(CultureInfo.InvariantCulture),
                Maximum = maximum?.ToString(CultureInfo.InvariantCulture),
                MaxLength = maxLength,
            },
        };
}