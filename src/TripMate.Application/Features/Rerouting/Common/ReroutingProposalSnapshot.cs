using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripMate.Application.Features.Rerouting.Common;

/// <summary>Writes every instant as UTC ISO 8601 with a trailing <c>Z</c>; reads any offset and normalizes to UTC.</summary>
internal sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(
            reader.GetString() ?? throw new JsonException("An instant is required."),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Strategy labels stored in the snapshot (spec 3.2 and 4.3).</summary>
public static class ReroutingStrategies
{
    public const string ReplaceWithIndoor = "ReplaceWithIndoor";
    public const string ReplaceSameCategory = "ReplaceSameCategory";
    public const string Drop = "Drop";
    public const string Postpone = "Postpone";
    public const string ReduceStops = "ReduceStops";
    public const string Reorder = "Reorder";

    public static readonly string[] All =
    [
        ReplaceWithIndoor,
        ReplaceSameCategory,
        Drop,
        Postpone,
        ReduceStops,
        Reorder,
    ];
}

/// <summary>How a proposed stop relates to the current plan.</summary>
public static class ReroutingStopChanges
{
    public const string Kept = "Kept";
    public const string Added = "Added";
    public const string Moved = "Moved";
}

public sealed record ReroutingAnchorSnapshot(
    DateTimeOffset StartAtUtc,
    decimal Latitude,
    decimal Longitude,
    int AvailableMinutes);

/// <summary>A Pending stop at generation time. Its item id is the staleness fence of an accept.</summary>
public sealed record ReroutingBaseStop(
    long ItineraryItemId,
    long PoiId,
    string Name,
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset PlannedArrivalUtc,
    DateTimeOffset PlannedDepartureUtc,
    bool IsMandatory);

public sealed record ReroutingProposedStop(
    long PoiId,
    string Name,
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset PlannedArrivalUtc,
    DateTimeOffset PlannedDepartureUtc,
    bool IsMandatory,
    string? TransportModeToNext,
    string Change);

/// <summary>
/// Contract of <c>trip.ReroutingEvents.proposed_itinerary_snapshot</c>, schema version 1. Rows without
/// a valid v1 shape (such as the UC-59 UAT demo rows) are "no valid proposal" and are never acted on.
/// </summary>
public sealed record ReroutingProposalSnapshot(
    int SchemaVersion,
    string Strategy,
    string ReasonCode,
    long BaseItineraryId,
    int BaseItineraryVersion,
    ReroutingAnchorSnapshot Anchor,
    IReadOnlyList<ReroutingBaseStop> BaseStops,
    IReadOnlyList<ReroutingProposedStop> ProposedStops,
    IReadOnlyList<long> RemovedPoiIds,
    IReadOnlyList<long> AffectedItineraryItemIds,
    int TravelTimeDiffMinutes,
    int ArrivalTimeDiffMinutes,
    DateTimeOffset EndAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new UtcDateTimeOffsetConverter() },
    };

    public static string Serialize(ReroutingProposalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    /// <summary>Returns the snapshot, or null for null/blank/malformed JSON, another schema version, or an invalid shape.</summary>
    public static ReroutingProposalSnapshot? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        ReroutingProposalSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ReroutingProposalSnapshot>(json, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }

        return snapshot is { SchemaVersion: CurrentSchemaVersion } && snapshot.IsValid() ? snapshot : null;
    }

    private bool IsValid() =>
        Array.IndexOf(ReroutingStrategies.All, Strategy) >= 0
        && !string.IsNullOrWhiteSpace(ReasonCode)
        && Anchor is not null
        && BaseStops is { Count: > 0 }
        && ProposedStops is { Count: > 0 }
        && RemovedPoiIds is not null
        && AffectedItineraryItemIds is not null;
}