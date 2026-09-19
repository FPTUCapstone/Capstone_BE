using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TripMate.Application.Features.Admin.AuditLogs.GetDetail;

/// <summary>BR-03 masking at the read boundary; never modifies the recorded audit entry.</summary>
internal static class AuditLogPayloadRedactor
{
    private const string Mask = "[REDACTED]";
    private const int MaxDepth = 32;
    private static readonly Regex CredentialAssignment = new(
        @"(?<label>[\p{L}][\p{L}\p{N}_-]*(?:[ \t]+[\p{L}][\p{L}\p{N}_-]*){0,3})[""']?\s*[:=]",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    public static string? Redact(string? payload) => Redact(payload, 0);

    public static string? RedactReason(string? reason)
    {
        if (reason is null)
        {
            return null;
        }

        // Check for JWT bearer tokens (eyJ prefix) or explicit Bearer header content.
        if (reason.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("eyJ", StringComparison.Ordinal))
        {
            return Mask;
        }

        // Find assignments before normalizing labels, including labels in the middle
        // of prose. Normalizing the entire reason discards the delimiters we need.
        try
        {
            foreach (Match match in CredentialAssignment.Matches(reason))
            {
                var label = match.Groups["label"].Value;
                var lastWord = label.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[^1];
                if (IsSensitive(label) || IsSensitive(lastWord) || Normalize(lastWord) == "card")
                {
                    return Mask;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Mask;
        }

        return reason;
    }

    private static string? Redact(string? payload, int depth)
    {
        if (payload is null)
        {
            return null;
        }

        if (depth >= MaxDepth)
        {
            return JsonSerializer.Serialize(Mask);
        }

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = MaxDepth });
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return JsonSerializer.Serialize(Mask);
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                Write(document.RootElement, writer, depth);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            // Never fall back to a raw payload that could contain legacy credentials.
            return JsonSerializer.Serialize(Mask);
        }
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer, int depth)
    {
        if (depth >= MaxDepth)
        {
            writer.WriteStringValue(Mask);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Also cover change-set representations: { field: "password", old: ..., new: ... }.
                if (element.EnumerateObject().Any(property =>
                    Normalize(property.Name) is "field" or "fieldname" or "path" or "property" or "propertyname" or "key" or "name"
                    && property.Value.ValueKind == JsonValueKind.String
                    && IsSensitive(property.Value.GetString()!)))
                {
                    writer.WriteStringValue(Mask);
                    break;
                }

                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitive(property.Name))
                    {
                        writer.WriteStringValue(Mask);
                    }
                    else
                    {
                        Write(property.Value, writer, depth + 1);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(item, writer, depth + 1);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var value = element.GetString()!;
                var trimmed = value.TrimStart();
                writer.WriteStringValue(trimmed.StartsWith('{') || trimmed.StartsWith('[')
                    ? Redact(value, depth + 1)
                    : value);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitive(string name)
    {
        var normalized = Normalize(name);
        return normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("passwd", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("cardnumber", StringComparison.Ordinal)
            || normalized.Contains("accountnumber", StringComparison.Ordinal)
            || normalized.Contains("privatekey", StringComparison.Ordinal)
            || normalized.Contains("signingkey", StringComparison.Ordinal)
            || normalized.Contains("cvv", StringComparison.Ordinal)
            || normalized.Contains("cvc", StringComparison.Ordinal)
            || normalized.Contains("securitycode", StringComparison.Ordinal)
            || normalized is "pwd" or "pan" or "pin" or "iban";
    }

    private static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}