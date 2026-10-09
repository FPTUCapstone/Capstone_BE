using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;

namespace TripMate.Api.OpenApi;

public sealed class TripReviewOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(TripReviewsController)
            && context.MethodInfo.DeclaringType != typeof(ServiceBookingTripReviewsController))
            return;

        if (context.MethodInfo.Name == nameof(TripReviewsController.Submit))
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Description = $"Exactly one JSON metadata part (maximum {TripReviewRequestReader.MaximumMetadataUtf8Bytes} UTF-8 bytes) and 0-{TripReviewInputRules.MaximumPhotos} files. Total body maximum {TripReviewRequestReader.MaximumPostBodyBytes} bytes.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["multipart/form-data"] = new()
                    {
                        Encoding = new Dictionary<string, OpenApiEncoding>
                        {
                            [TripReviewRequestReader.MetadataPartName] = new()
                            {
                                ContentType = "application/json; charset=utf-8",
                            },
                        },
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            AdditionalPropertiesAllowed = false,
                            Required = new HashSet<string>(StringComparer.Ordinal)
                            {
                                TripReviewRequestReader.MetadataPartName,
                            },
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                [TripReviewRequestReader.MetadataPartName] = context.SchemaGenerator.GenerateSchema(
                                    typeof(SubmitTripReviewMetadataRequest), context.SchemaRepository),
                                [TripReviewRequestReader.FilesPartName] = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.Array,
                                    MinItems = 0,
                                    MaxItems = TripReviewInputRules.MaximumPhotos,
                                    Items = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Format = "binary",
                                        MaxLength = checked((int)TripReviewInputRules.MaximumPhotoBytes),
                                    },
                                },
                            },
                        },
                    },
                },
            };
        }
        else if (context.MethodInfo.Name == nameof(TripReviewsController.Edit))
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = context.SchemaGenerator.GenerateSchema(
                            typeof(EditTripReviewRequest), context.SchemaRepository),
                    },
                },
            };
        }

        NormalizeProblemResponses(operation, context);
    }

    private static void NormalizeProblemResponses(
        OpenApiOperation operation,
        OperationFilterContext context)
    {
        if (operation.Responses is null)
            return;

        foreach (var response in operation.Responses
                     .Where(item => !item.Key.StartsWith('2'))
                     .Select(item => item.Value))
        {
            var content = response.Content;
            if (content is null)
                continue;

            var schema = content.TryGetValue("application/json", out var json)
                ? json.Schema
                : content.Values.FirstOrDefault()?.Schema;
            if (schema is null)
                continue;

            content.Clear();
            content["application/problem+json"] = new OpenApiMediaType
            {
                Schema = schema,
            };
        }

        if (operation.Responses.TryGetValue("400", out var badRequest)
            && badRequest.Content is not null
            && badRequest.Content.TryGetValue("application/problem+json", out var problemMediaType))
        {
            problemMediaType.Schema = new OpenApiSchema
            {
                OneOf =
                [
                    context.SchemaGenerator.GenerateSchema(
                        typeof(ErrorCodeProblemDetails), context.SchemaRepository),
                    context.SchemaGenerator.GenerateSchema(
                        typeof(ErrorCodeValidationProblemDetails), context.SchemaRepository),
                ],
            };
        }
    }
}

public sealed class TripReviewRequestSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema mutableSchema)
        {
            return;
        }

        if (context.Type == typeof(SubmitTripReviewPoiRequest))
        {
            mutableSchema.AdditionalPropertiesAllowed = false;
            Require(mutableSchema,
                WireName(nameof(SubmitTripReviewPoiRequest.PoiId)),
                WireName(nameof(SubmitTripReviewPoiRequest.Rating)));
            MutableProperty(mutableSchema,
                WireName(nameof(SubmitTripReviewPoiRequest.PoiId))).Minimum = "1";
            ConstrainRating(MutableProperty(mutableSchema,
                WireName(nameof(SubmitTripReviewPoiRequest.Rating))));
            return;
        }

        if (context.Type == typeof(TripReviewReadDto))
        {
            var currentSchema = context.SchemaGenerator.GenerateSchema(
                typeof(NewTripReviewDto), context.SchemaRepository);
            var legacySchema = context.SchemaGenerator.GenerateSchema(
                typeof(LegacyTripReviewDto), context.SchemaRepository);
            mutableSchema.OneOf =
            [
                currentSchema,
                legacySchema,
            ];
            mutableSchema.Discriminator = new OpenApiDiscriminator
            {
                PropertyName = WireName(nameof(TripReviewReadDto.Kind)),
                Mapping = new Dictionary<string, OpenApiSchemaReference>
                {
                    [TripReviewContextValues.New] = SchemaReference(
                        currentSchema, nameof(NewTripReviewDto)),
                    [TripReviewContextValues.Legacy] = SchemaReference(
                        legacySchema, nameof(LegacyTripReviewDto)),
                },
            };
            return;
        }

        if (context.Type == typeof(NewTripReviewDto))
        {
            ConstrainDiscriminatorValue(
                mutableSchema, TripReviewContextValues.New);
            ConstrainUtcTimestamp(mutableSchema, nameof(NewTripReviewDto.CreatedAtUtc));
            ConstrainUtcTimestamp(mutableSchema, nameof(NewTripReviewDto.EditDeadlineUtc));
            ConstrainUtcTimestamp(mutableSchema, nameof(NewTripReviewDto.UpdatedAtUtc));
            return;
        }

        if (context.Type == typeof(LegacyTripReviewEntryDto))
        {
            ConstrainUtcTimestamp(mutableSchema, nameof(LegacyTripReviewEntryDto.CreatedAtUtc));
            return;
        }

        if (context.Type == typeof(LegacyTripReviewDto))
        {
            ConstrainDiscriminatorValue(
                mutableSchema, TripReviewContextValues.Legacy);
            return;
        }

        if (context.Type != typeof(SubmitTripReviewMetadataRequest)
            && context.Type != typeof(EditTripReviewRequest))
        {
            return;
        }

        mutableSchema.AdditionalPropertiesAllowed = false;
        ConstrainRating(MutableProperty(mutableSchema,
            WireName(nameof(SubmitTripReviewMetadataRequest.OverallRating))));
        ConstrainText(MutableProperty(mutableSchema,
            WireName(nameof(SubmitTripReviewMetadataRequest.Title))), TripReview.TitleMaxLength);
        ConstrainText(MutableProperty(mutableSchema,
            WireName(nameof(SubmitTripReviewMetadataRequest.Content))), TripReview.ContentMaxLength);

        if (context.Type == typeof(SubmitTripReviewMetadataRequest))
        {
            var propertyName = WireName(
                nameof(SubmitTripReviewMetadataRequest.RoutePacing));
            if (mutableSchema.Properties?.TryGetValue(propertyName, out var property) == true
                && property is OpenApiSchema mutableProperty)
            {
                mutableProperty.Enum =
                [
                    JsonValue.Create(TripReviewContextValues.TooTight),
                    JsonValue.Create(TripReviewContextValues.WellPaced),
                    JsonValue.Create(TripReviewContextValues.TooLoose),
                ];
            }

            ConstrainRating(MutableProperty(mutableSchema,
                WireName(nameof(SubmitTripReviewMetadataRequest.CspRating))));
            MutableProperty(mutableSchema,
                WireName(nameof(SubmitTripReviewMetadataRequest.PoiRatings))).Description =
                "Distinct positive POI IDs; each rating is from 1 to 5.";
            return;
        }

        var version = MutableProperty(mutableSchema,
            WireName(nameof(EditTripReviewRequest.Version)));
        version.MinLength = 12;
        version.MaxLength = 12;
        version.Pattern = "^[A-Za-z0-9+/]{10}[AEIMQUYcgkosw048]=$";
        version.Description = "Canonical Base64 encoding of the 8 raw SQL rowversion bytes.";
    }

    private static OpenApiSchema MutableProperty(OpenApiSchema schema, string name)
    {
        if (schema.Properties?.TryGetValue(name, out var property) != true
            || property is not OpenApiSchema mutableProperty)
        {
            throw new InvalidOperationException(
                $"Trip review OpenAPI property '{name}' was not generated.");
        }

        return mutableProperty;
    }

    private static void Require(OpenApiSchema schema, params string[] names)
    {
        schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
            schema.Required.Add(name);
    }

    private static void ConstrainRating(OpenApiSchema schema)
    {
        schema.Minimum = TripReviewInputRules.MinimumRating.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        schema.Maximum = TripReviewInputRules.MaximumRating.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ConstrainText(OpenApiSchema schema, int maximum)
    {
        schema.MinLength = 1;
        schema.MaxLength = maximum;
        schema.Description = "Trimmed text; whitespace-only input is invalid.";
    }

    private static void ConstrainUtcTimestamp(OpenApiSchema schema, string memberName)
    {
        var property = MutableProperty(schema, WireName(memberName));
        property.Format = "date-time";
        property.Pattern = "Z$";
        property.Description = "ISO 8601 UTC timestamp with a trailing Z.";
    }

    private static void ConstrainDiscriminatorValue(OpenApiSchema schema, string value)
    {
        var propertyName = WireName(nameof(TripReviewReadDto.Kind));
        Require(schema, propertyName);
        MutableProperty(schema, propertyName).Enum = [JsonValue.Create(value)];
    }

    private static OpenApiSchemaReference SchemaReference(
        IOpenApiSchema schema,
        string typeName) => schema as OpenApiSchemaReference
        ?? throw new InvalidOperationException(
            $"Trip review OpenAPI schema '{typeName}' was not registered as a component reference.");

    private static string WireName(string memberName) =>
        JsonNamingPolicy.CamelCase.ConvertName(memberName);
}