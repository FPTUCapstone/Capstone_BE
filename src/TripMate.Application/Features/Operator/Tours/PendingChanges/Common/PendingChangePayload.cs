using System.Text.Json;

using TripMate.Application.Features.Operator.Tours.Create;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Common;

/// <summary>
/// A proposed departure inside a TourPendingChangeSet's SchedulesJson payload. A null
/// <see cref="ScheduleId"/> means a newly proposed schedule; a non-null id must match an existing
/// TourSchedule.Id belonging to the same tour (spec TM-82 §3 point G6) and is updated in place by
/// Tour.ApplyPendingChange (plan amendment B1) rather than replaced.
/// </summary>
public sealed record PendingChangeScheduleItem(
    long? ScheduleId,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string? MeetingPoint,
    int TotalCapacity,
    int MinimumCapacity = 1,
    DateTimeOffset? RegistrationDeadlineAtUtc = null,
    decimal? AdultPrice = null,
    decimal? ChildPrice = null,
    decimal? InfantPrice = null,
    decimal? MeetingLatitude = null,
    decimal? MeetingLongitude = null);

/// <summary>Serializes/deserializes a TourPendingChangeSet's JSON payload columns (spec §4.1).</summary>
public static class PendingChangePayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string SerializeDestinationIds(IEnumerable<long> destinationIds) =>
        JsonSerializer.Serialize(destinationIds, Options);

    public static IReadOnlyList<long> DeserializeDestinationIds(string json) =>
        JsonSerializer.Deserialize<List<long>>(json, Options) ?? [];

    public static string SerializeItineraryItems(IEnumerable<CreateTourItineraryItemRequest> items) =>
        JsonSerializer.Serialize(items, Options);

    public static IReadOnlyList<CreateTourItineraryItemRequest> DeserializeItineraryItems(string json) =>
        JsonSerializer.Deserialize<List<CreateTourItineraryItemRequest>>(json, Options) ?? [];

    public static string SerializeSchedules(IEnumerable<PendingChangeScheduleItem> schedules) =>
        JsonSerializer.Serialize(schedules, Options);

    public static IReadOnlyList<PendingChangeScheduleItem> DeserializeSchedules(string json) =>
        JsonSerializer.Deserialize<List<PendingChangeScheduleItem>>(json, Options) ?? [];
}
