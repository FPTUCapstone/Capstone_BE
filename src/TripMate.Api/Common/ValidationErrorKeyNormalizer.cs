using System.Text.Json;

namespace TripMate.Api.Common;

internal static class ValidationErrorKeyNormalizer
{
    public static IDictionary<string, string[]> Normalize(
        IEnumerable<KeyValuePair<string, string[]>> errors,
        JsonNamingPolicy? namingPolicy) =>
        errors
            .GroupBy(error => ConvertPropertyPath(error.Key, namingPolicy))
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(error => error.Value).ToArray(),
                StringComparer.Ordinal);

    public static void NormalizeInPlace(
        IDictionary<string, string[]> errors,
        JsonNamingPolicy? namingPolicy)
    {
        var normalizedErrors = Normalize(errors, namingPolicy);

        errors.Clear();
        foreach (var error in normalizedErrors)
        {
            errors.Add(error);
        }
    }

    private static string ConvertPropertyPath(
        string propertyPath,
        JsonNamingPolicy? namingPolicy)
    {
        if (namingPolicy is null)
        {
            return propertyPath;
        }

        return string.Join(
            '.',
            propertyPath.Split('.').Select(segment => ConvertPathSegment(segment, namingPolicy)));
    }

    private static string ConvertPathSegment(string segment, JsonNamingPolicy namingPolicy)
    {
        var indexStart = segment.IndexOf('[');
        if (indexStart < 0)
        {
            return namingPolicy.ConvertName(segment);
        }

        var propertyName = segment[..indexStart];
        return namingPolicy.ConvertName(propertyName) + segment[indexStart..];
    }
}