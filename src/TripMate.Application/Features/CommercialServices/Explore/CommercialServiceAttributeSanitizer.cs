using System.Text.Json;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.CommercialServices.Explore;

internal static class CommercialServiceAttributeSanitizer
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedKeys =
        new Dictionary<string, string[]>
        {
            [CommercialService.CategoryVehicle] =
            [
                "transmission",
                "seats",
                "licenseRequired",
                "luggageCapacity"
            ],
            [CommercialService.CategoryHotel] =
            [
                "roomType",
                "beds",
                "checkInTime",
                "checkOutTime"
            ],
            [CommercialService.CategoryRestaurant] =
            [
                "cuisine",
                "servingSize",
                "reservationType"
            ],
        };

    public static IReadOnlyDictionary<string, object?> Sanitize(
        string category,
        string? attributesJson)
    {
        if (string.IsNullOrWhiteSpace(attributesJson)
            || !AllowedKeys.TryGetValue(category, out var allowedKeys))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using var document = JsonDocument.Parse(attributesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>();
            }

            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowedKeys.Contains(property.Name)
                    || !TryReadPublicValue(property.Value, out var value))
                {
                    continue;
                }

                attributes[property.Name] = value;
            }

            return attributes;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static bool TryReadPublicValue(JsonElement value, out object? result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                result = value.GetString();
                return result is not null;
            case JsonValueKind.True:
                result = true;
                return true;
            case JsonValueKind.False:
                result = false;
                return true;
            case JsonValueKind.Number when value.TryGetInt32(out var integer):
                result = integer;
                return true;
            case JsonValueKind.Number when value.TryGetDecimal(out var decimalValue):
                result = decimalValue;
                return true;
            default:
                result = null;
                return false;
        }
    }
}