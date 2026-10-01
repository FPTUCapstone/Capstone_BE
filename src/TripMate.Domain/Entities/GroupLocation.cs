using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class GroupLocation : BaseEntity
{
    private GroupLocation()
    {
    }

    private GroupLocation(long groupId, long userId, decimal latitude, decimal longitude, DateTimeOffset recordedAtUtc)
    {
        if (groupId <= 0 || userId <= 0 || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "A valid group, member and GPS coordinate are required.");
        }

        GroupId = groupId;
        UserId = userId;
        Latitude = latitude;
        Longitude = longitude;
        RecordedAtUtc = recordedAtUtc;
    }

    public static GroupLocation Create(long groupId, long userId, decimal latitude, decimal longitude, DateTimeOffset recordedAtUtc)
        => new(groupId, userId, latitude, longitude, recordedAtUtc);

    public long GroupId { get; private set; }
    public long UserId { get; private set; }
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
}