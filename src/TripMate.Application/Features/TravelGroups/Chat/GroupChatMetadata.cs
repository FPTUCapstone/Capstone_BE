using System.Text.Json;
using System.Text.RegularExpressions;

namespace TripMate.Application.Features.TravelGroups.Chat;

internal static partial class GroupChatMetadata
{
    public const int MaxLength = 16000;
    private const long MaxSafeAmount = 9007199254740991L;
    private const int MaxSplitBillParticipants = 100;

    [GeneratedRegex(@"^\d{6,30}$")]
    private static partial Regex BankAccountRegex();

    public static bool IsValid(string? metadata, string? messageType)
    {
        var normalizedType = messageType?.Trim().ToLowerInvariant();
        if (metadata is null)
        {
            return normalizedType is not "poll" and not "split_bill";
        }

        if (metadata.Length > MaxLength) return false;
        try
        {
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            return normalizedType switch
            {
                "poll" => TryGetPollOptionCount(root, out _),
                "split_bill" => TryParseSplitBillParticipants(root, out _),
                "broadcast" => IsValidBroadcast(root),
                "roll_call" => IsValidRollCall(root),
                _ => true,
            };
        }
        catch (JsonException) { return false; }
    }

    public static bool TryGetPollOptionCount(string? metadata, out int count)
    {
        count = 0;
        if (metadata is null || metadata.Length > MaxLength) return false;
        try
        {
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
            return TryGetPollOptionCount(document.RootElement, out count);
        }
        catch (JsonException) { return false; }
    }

    public static bool TryGetSplitBillParticipantIds(string? metadata, out IReadOnlyList<long> participantUserIds)
    {
        participantUserIds = [];
        if (metadata is null || metadata.Length > MaxLength) return false;
        try
        {
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
            return TryParseSplitBillParticipants(document.RootElement, out participantUserIds);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryGetPollOptionCount(JsonElement root, out int count)
    {
        count = 0;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array
            || options.GetArrayLength() is < 2 or > 6)
        {
            return false;
        }

        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object
                || !option.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = text.GetString()?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > 200)
            {
                return false;
            }
        }

        count = options.GetArrayLength();
        return true;
    }

    private static bool TryParseSplitBillParticipants(JsonElement root, out IReadOnlyList<long> participantUserIds)
    {
        participantUserIds = [];
        if (root.ValueKind != JsonValueKind.Object) return false;

        if (!TryGetWholeInt64(root, "totalAmount", out var totalAmount)
            || totalAmount <= 0
            || totalAmount > MaxSafeAmount)
        {
            return false;
        }

        if (!TryGetNonBlankString(root, "payerName", 120, out _)
            || !TryGetNonBlankString(root, "payerBankName", 60, out _)
            || !TryGetNonBlankString(root, "payerBankAccount", 30, out var bankAccount)
            || !BankAccountRegex().IsMatch(bankAccount))
        {
            return false;
        }

        if (!root.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Array
            || participants.GetArrayLength() is < 1 or > MaxSplitBillParticipants)
        {
            return false;
        }

        var seenUsers = new HashSet<long>();
        var ids = new List<long>(participants.GetArrayLength());
        long sum = 0;

        foreach (var participant in participants.EnumerateArray())
        {
            if (participant.ValueKind != JsonValueKind.Object) return false;
            if (!TryGetWholeInt64(participant, "userId", out var userId) || userId <= 0 || !seenUsers.Add(userId))
            {
                return false;
            }

            if (!TryGetNonBlankString(participant, "displayName", 120, out _))
            {
                return false;
            }

            if (!TryGetWholeInt64(participant, "amount", out var amount)
                || amount < 0
                || amount > MaxSafeAmount)
            {
                return false;
            }

            if (sum > MaxSafeAmount - amount)
            {
                return false;
            }

            sum += amount;
            ids.Add(userId);
        }

        if (sum != totalAmount)
        {
            return false;
        }

        participantUserIds = ids;
        return true;
    }

    private static bool IsValidBroadcast(JsonElement root)
    {
        if (root.TryGetProperty("title", out var title) && title.ValueKind != JsonValueKind.Null)
        {
            if (title.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(title.GetString())
                || title.GetString()!.Trim().Length > 200)
            {
                return false;
            }
        }

        if (root.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
        {
            if (content.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(content.GetString())
                || content.GetString()!.Trim().Length > 4000)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidRollCall(JsonElement root)
    {
        if (root.TryGetProperty("title", out var title) && title.ValueKind != JsonValueKind.Null)
        {
            if (title.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(title.GetString())
                || title.GetString()!.Trim().Length > 200)
            {
                return false;
            }
        }

        if (root.TryGetProperty("totalMembers", out var totalMembers) && totalMembers.ValueKind != JsonValueKind.Null)
        {
            if (totalMembers.ValueKind != JsonValueKind.Number
                || !totalMembers.TryGetInt64(out var count)
                || count < 1)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetWholeInt64(JsonElement obj, string propertyName, out long value)
    {
        value = 0;
        return obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt64(out value);
    }

    private static bool TryGetNonBlankString(JsonElement obj, string propertyName, int maxLength, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var trimmed = prop.GetString()?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength)
        {
            return false;
        }

        value = trimmed;
        return true;
    }

    // Canonicalize JSON object order and whitespace for retry comparisons; preserve stored metadata verbatim.
    public static string? Normalize(string? metadata)
    {
        if (metadata is null) return null;
        using var document = JsonDocument.Parse(metadata);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(document.RootElement, writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(item, writer);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}