using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Primitives;

using TripMate.Application.Features.Tours.Search;

namespace TripMate.Api.Controllers.V1.Requests;

internal static class TourSearchQueryParser
{
    private static readonly HashSet<string> SupportedKeys = new(
    [
        "destination",
        "departureDate",
        "minPrice",
        "maxPrice",
        "page",
        "pageSize",
    ],
        StringComparer.Ordinal);

    public static bool TryParse(
        IQueryCollection source,
        ModelStateDictionary modelState,
        out SearchToursQuery query)
    {
        foreach (var key in source.Keys.Where(key => !SupportedKeys.Contains(key)))
        {
            var canonical = SupportedKeys.FirstOrDefault(
                candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null)
            {
                modelState.AddModelError(canonical, $"Use canonical query parameter '{canonical}' once.");
            }
            else
            {
                modelState.AddModelError("query", $"Unsupported query parameter '{key}'.");
            }
        }

        TryReadSingle(source, modelState, "destination", out var destination);
        var departureDate = ParseDate(source, modelState, "departureDate");
        var minPrice = ParseInt64(source, modelState, "minPrice");
        var maxPrice = ParseInt64(source, modelState, "maxPrice");
        var page = ParseInt32(source, modelState, "page") ?? 1;
        var pageSize = ParseInt32(source, modelState, "pageSize") ?? 20;

        query = new SearchToursQuery(
            destination,
            departureDate,
            minPrice,
            maxPrice,
            page,
            pageSize);
        return modelState.IsValid;
    }

    private static DateOnly? ParseDate(
        IQueryCollection source,
        ModelStateDictionary modelState,
        string key)
    {
        if (!TryReadSingle(source, modelState, key, out var value) || value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            modelState.AddModelError(key, "Departure date must use ISO format yyyy-MM-dd.");
            return null;
        }

        return parsed;
    }

    private static long? ParseInt64(
        IQueryCollection source,
        ModelStateDictionary modelState,
        string key)
    {
        if (!TryReadSingle(source, modelState, key, out var value) || value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!value.All(char.IsAsciiDigit)
            || !long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            modelState.AddModelError(key, $"{key} must contain only decimal digits.");
            return null;
        }

        return parsed;
    }

    private static int? ParseInt32(
        IQueryCollection source,
        ModelStateDictionary modelState,
        string key)
    {
        if (!TryReadSingle(source, modelState, key, out var value) || value is null)
        {
            return null;
        }

        if (!value.All(char.IsAsciiDigit)
            || !int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            modelState.AddModelError(key, $"{key} must contain only decimal digits.");
            return null;
        }

        return parsed;
    }

    private static bool TryReadSingle(
        IQueryCollection source,
        ModelStateDictionary modelState,
        string key,
        out string? value)
    {
        value = null;
        if (!source.TryGetValue(key, out StringValues values))
        {
            return true;
        }

        if (values.Count != 1)
        {
            modelState.AddModelError(key, $"Query parameter '{key}' must be provided once.");
            return false;
        }

        value = values[0];
        return true;
    }
}