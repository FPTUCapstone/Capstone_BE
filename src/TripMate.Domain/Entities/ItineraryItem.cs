using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public class ItineraryItem : BaseEntity
{
    public const int RecommendationReasonMaxLength = 500;

    private ItineraryItem()
    {
    }

    public long ItineraryId { get; private set; }

    public Itinerary Itinerary { get; private set; } = null!;

    public long? PointOfInterestId { get; private set; }

    public PointOfInterest? PointOfInterest { get; private set; }

    public int SequenceNo { get; private set; }

    public ItineraryItemKind Kind { get; private set; }

    public DateTimeOffset PlannedArrivalUtc { get; private set; }

    public DateTimeOffset PlannedDepartureUtc { get; private set; }

    public int StayDurationMinutes { get; private set; }

    public bool IsMandatory { get; private set; }

    public decimal? EstimatedCost { get; private set; }

    public string? RecommendationReason { get; private set; }

    public static ItineraryItem CreateVisit(
        int sequenceNo,
        long pointOfInterestId,
        DateTimeOffset plannedArrivalUtc,
        DateTimeOffset plannedDepartureUtc,
        bool isMandatory,
        decimal? estimatedCost,
        string? recommendationReason)
    {
        if (pointOfInterestId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pointOfInterestId));
        }

        if (estimatedCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedCost));
        }

        return Create(
            sequenceNo,
            ItineraryItemKind.Visit,
            pointOfInterestId,
            plannedArrivalUtc,
            plannedDepartureUtc,
            isMandatory,
            estimatedCost,
            recommendationReason);
    }

    public static ItineraryItem CreateRest(
        int sequenceNo,
        DateTimeOffset plannedArrivalUtc,
        DateTimeOffset plannedDepartureUtc,
        string recommendationReason) =>
        CreateRest(
            sequenceNo,
            null,
            plannedArrivalUtc,
            plannedDepartureUtc,
            recommendationReason);

    public static ItineraryItem CreateRest(
        int sequenceNo,
        long? pointOfInterestId,
        DateTimeOffset plannedArrivalUtc,
        DateTimeOffset plannedDepartureUtc,
        string recommendationReason)
    {
        if (pointOfInterestId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pointOfInterestId));
        }

        return Create(
            sequenceNo,
            ItineraryItemKind.Rest,
            pointOfInterestId,
            plannedArrivalUtc,
            plannedDepartureUtc,
            false,
            null,
            recommendationReason);
    }

    internal void AttachTo(Itinerary itinerary)
    {
        Itinerary = itinerary ?? throw new ArgumentNullException(nameof(itinerary));
    }

    private static ItineraryItem Create(
        int sequenceNo,
        ItineraryItemKind kind,
        long? pointOfInterestId,
        DateTimeOffset plannedArrivalUtc,
        DateTimeOffset plannedDepartureUtc,
        bool isMandatory,
        decimal? estimatedCost,
        string? recommendationReason)
    {
        if (sequenceNo <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNo));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (plannedDepartureUtc <= plannedArrivalUtc)
        {
            throw new ArgumentException(
                "A planned departure must be after planned arrival.",
                nameof(plannedDepartureUtc));
        }

        if (kind == ItineraryItemKind.Visit && !pointOfInterestId.HasValue)
        {
            throw new ArgumentException("A visit requires a point of interest.", nameof(pointOfInterestId));
        }

        if (kind == ItineraryItemKind.Rest && isMandatory)
        {
            throw new ArgumentException("A rest item cannot be mandatory.");
        }

        var normalizedReason = NormalizeOptional(recommendationReason);
        var duration = checked((int)(plannedDepartureUtc - plannedArrivalUtc).TotalMinutes);

        return new ItineraryItem
        {
            SequenceNo = sequenceNo,
            Kind = kind,
            PointOfInterestId = pointOfInterestId,
            PlannedArrivalUtc = plannedArrivalUtc,
            PlannedDepartureUtc = plannedDepartureUtc,
            StayDurationMinutes = duration,
            IsMandatory = isMandatory,
            EstimatedCost = estimatedCost,
            RecommendationReason = normalizedReason,
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > RecommendationReasonMaxLength)
        {
            throw new ArgumentException(
                $"Recommendation reason cannot exceed {RecommendationReasonMaxLength} characters.",
                nameof(value));
        }

        return normalized;
    }
}
