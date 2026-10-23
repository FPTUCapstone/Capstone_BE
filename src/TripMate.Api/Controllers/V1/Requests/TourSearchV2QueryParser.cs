using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Mvc.ModelBinding;

using TripMate.Application.Features.Tours.Search.V2;

namespace TripMate.Api.Controllers.V1.Requests;

/// <summary>Prepared parser only; the production Tours controller retains its legacy contract.</summary>
public static class TourSearchV2QueryParser
{
    private static readonly string[] Keys =
    [
        "keyword", "destination", "startDate", "endDate", "minPrice", "maxPrice",
        "durationDays", "category", "minimumRating", "sort", "page", "pageSize",
    ];

    public static bool TryParse(IQueryCollection source, ModelStateDictionary state, out TourSearchV2Request request)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in source)
        {
            if (!Keys.Contains(entry.Key, StringComparer.Ordinal))
            {
                var canonical = Keys.FirstOrDefault(key => string.Equals(key, entry.Key, StringComparison.OrdinalIgnoreCase));
                state.AddModelError(canonical ?? "query", $"Unsupported or noncanonical query parameter '{entry.Key}'.");
                continue;
            }
            if (entry.Value.Count != 1)
            {
                state.AddModelError(entry.Key, $"Query parameter '{entry.Key}' must be provided once.");
                continue;
            }
            values.Add(entry.Key, entry.Value[0]);
        }

        string? Text(string key, bool nfc = true)
        {
            var value = values.GetValueOrDefault(key)?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            try { return nfc ? value.Normalize(NormalizationForm.FormC) : value; }
            catch (ArgumentException) { state.AddModelError(key, "Invalid Unicode text."); return null; }
        }

        long? Integer(string key)
        {
            if (!values.TryGetValue(key, out var value)) return null;
            if (string.IsNullOrEmpty(value) || !value.All(char.IsAsciiDigit)
                || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                state.AddModelError(key, "Value must contain decimal digits within the integer representation.");
                return null;
            }
            return parsed;
        }

        int? Int32(string key)
        {
            var parsed = Integer(key);
            if (parsed > int.MaxValue) { state.AddModelError(key, "Value exceeds the 32-bit integer representation."); return null; }
            return (int?)parsed;
        }

        DateOnly? Date(string key)
        {
            if (!values.TryGetValue(key, out var value)) return null;
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            { state.AddModelError(key, "Date must use ISO format yyyy-MM-dd."); return null; }
            return parsed;
        }

        decimal? Rating()
        {
            const string key = "minimumRating";
            if (!values.TryGetValue(key, out var value)) return null;
            if (string.IsNullOrEmpty(value) || value.Count(character => character == '.') > 1
                || value.Any(character => !char.IsAsciiDigit(character) && character != '.')
                || !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
                || CanonicalDecimal(value) != CanonicalDecimal(parsed.ToString(CultureInfo.InvariantCulture)))
            { state.AddModelError(key, "Rating must be a decimal number."); return null; }
            return parsed;
        }

        var keyword = Text("keyword");
        var sort = values.GetValueOrDefault("sort") ?? (keyword is null
            ? TourSearchV2Request.SortDepartureDate : TourSearchV2Request.SortRelevance);
        if (keyword is null && sort == TourSearchV2Request.SortRelevance) sort = TourSearchV2Request.SortDepartureDate;
        request = new TourSearchV2Request(keyword, Text("destination"), Date("startDate"), Date("endDate"),
            Integer("minPrice"), Integer("maxPrice"), Int32("durationDays"), Text("category", false), Rating(),
            sort, Int32("page") ?? 1, Int32("pageSize") ?? 20);
        return state.IsValid;
    }

    private static string CanonicalDecimal(string value)
    {
        var parts = value.Split('.');
        var integer = parts[0].TrimStart('0');
        var fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : string.Empty;
        return (integer.Length == 0 ? "0" : integer) + (fraction.Length == 0 ? "" : "." + fraction);
    }
}