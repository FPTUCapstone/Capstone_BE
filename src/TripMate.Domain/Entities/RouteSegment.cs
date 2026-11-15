using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>Stored route metrics. Guidance and preview geometry belong to the map provider (BR-35).</summary>
public sealed class RouteSegment : BaseEntity
{
    public const int NoteMaxLength = 2_000;
    public const decimal MaximumDistanceKm = 9_999.99m;
    private RouteSegment() { }

    public long FromPoiId { get; private set; }
    public PointOfInterest FromPoi { get; private set; } = null!;
    public long ToPoiId { get; private set; }
    public PointOfInterest ToPoi { get; private set; } = null!;
    public decimal DistanceKm { get; private set; }
    public int EstimatedDurationMinutes { get; private set; }
    public TransportMode TransportMode { get; private set; }
    public RouteStatus Status { get; private set; }
    public string? RouteNote { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public long? CreatedById { get; private set; }
    public Guid? IdempotencyKey { get; private set; }

    public static RouteSegment Create(PointOfInterest from, PointOfInterest to,
        decimal distanceKm, int estimatedDurationMinutes, TransportMode transportMode,
        string? routeNote, long createdById, Guid idempotencyKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Id <= 0 || to.Id <= 0 || from.Id == to.Id
            || from.Status != PointOfInterestStatus.Active || to.Status != PointOfInterestStatus.Active)
            throw new ArgumentException("Routes must connect distinct Active points of interest.");
        if (distanceKm <= 0 || distanceKm > MaximumDistanceKm || decimal.Round(distanceKm, 2) != distanceKm)
            throw new ArgumentOutOfRangeException(nameof(distanceKm));
        if (estimatedDurationMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(estimatedDurationMinutes));
        if (!Enum.IsDefined(transportMode)) throw new ArgumentOutOfRangeException(nameof(transportMode));
        if (routeNote?.Trim().Length > NoteMaxLength) throw new ArgumentException("Route note exceeds storage limit.", nameof(routeNote));
        if (createdById <= 0) throw new ArgumentOutOfRangeException(nameof(createdById));
        if (idempotencyKey == Guid.Empty) throw new ArgumentException("An operation identifier is required.", nameof(idempotencyKey));
        return new RouteSegment
        {
            FromPoi = from,
            FromPoiId = from.Id,
            ToPoi = to,
            ToPoiId = to.Id,
            DistanceKm = distanceKm,
            EstimatedDurationMinutes = estimatedDurationMinutes,
            TransportMode = transportMode,
            Status = RouteStatus.Active,
            RouteNote = string.IsNullOrWhiteSpace(routeNote) ? null : routeNote.Trim(),
            CreatedById = createdById,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now.ToUniversalTime(),
        };
    }
}