namespace TripMate.Domain.Entities;

/// <summary>A POI stop in an operator-owned commercial tour draft.</summary>
public sealed class TourItineraryItem
{
    public const int NotesMaxLength = 500;

    private TourItineraryItem() { }

    public long Id { get; private set; }
    public long TourId { get; private set; }
    public Tour Tour { get; private set; } = null!;
    public int DayNo { get; private set; }
    public int SequenceNo { get; private set; }
    public long PoiId { get; private set; }
    public PointOfInterest Poi { get; private set; } = null!;
    public TimeOnly? PlannedTime { get; private set; }
    public int StayDurationMinutes { get; private set; }
    public string? Notes { get; private set; }

    public static TourItineraryItem Create(int dayNo, int sequenceNo, long poiId,
        TimeOnly? plannedTime, int stayDurationMinutes, string? notes)
    {
        if (dayNo <= 0) throw new ArgumentOutOfRangeException(nameof(dayNo));
        if (sequenceNo <= 0) throw new ArgumentOutOfRangeException(nameof(sequenceNo));
        if (poiId <= 0) throw new ArgumentOutOfRangeException(nameof(poiId));
        if (stayDurationMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(stayDurationMinutes));
        var normalizedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (normalizedNotes?.Length > NotesMaxLength) throw new ArgumentOutOfRangeException(nameof(notes));
        return new TourItineraryItem
        {
            DayNo = dayNo,
            SequenceNo = sequenceNo,
            PoiId = poiId,
            PlannedTime = plannedTime,
            StayDurationMinutes = stayDurationMinutes,
            Notes = normalizedNotes,
        };
    }

    internal void AttachTo(Tour tour)
    {
        Tour = tour ?? throw new ArgumentNullException(nameof(tour));
    }
}