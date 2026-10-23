namespace TripMate.Application.Features.Operator.Tours.Common;

/// <summary>Stable, non-blocking itinerary warning codes (spec: warnings never block a Draft save).</summary>
public static class TourItineraryWarningCodes
{
    public const string DuplicatePoi = "duplicate_poi";
    public const string PoiClosedOnDay = "poi_closed_on_day";
    public const string OutsideOpeningHours = "outside_opening_hours";
    public const string OpeningHoursUnknown = "opening_hours_unknown";
}

public sealed record OperatorTourWarningDto(string Code, int? DayNo, int? SequenceNo, long? PoiId);

public sealed record TourItineraryItemFact(
    int DayNo, int SequenceNo, long PoiId, TimeOnly? PlannedTime, int StayDurationMinutes);

/// <param name="DayOfWeek">0 = Sunday ... 6 = Saturday, as stored in <c>PoiOpeningHours</c>.</param>
public sealed record PoiOpeningHoursFact(long PoiId, byte DayOfWeek, TimeOnly? OpenTime, TimeOnly? CloseTime, bool IsClosed);

/// <summary>
/// Pure evaluation of itinerary feasibility warnings. Only the facts the system actually holds are
/// judged: missing opening-hours data yields an explicit "unknown" warning, never an invented answer,
/// and no route/travel-time estimate is made (no routing data is stored for tours).
/// </summary>
public static class TourItineraryWarningEvaluator
{
    public static IReadOnlyList<OperatorTourWarningDto> Evaluate(
        IReadOnlyList<TourItineraryItemFact> items,
        DateOnly? firstDepartureLocalDate,
        IReadOnlyList<PoiOpeningHoursFact> openingHours)
    {
        var warnings = new List<OperatorTourWarningDto>();
        var ordered = items.OrderBy(item => item.DayNo).ThenBy(item => item.SequenceNo).ToList();

        foreach (var group in ordered.GroupBy(item => item.PoiId).Where(group => group.Count() > 1))
        {
            // Report each repeat after the first so the operator can find the duplicate stop.
            foreach (var repeat in group.Skip(1))
            {
                warnings.Add(new OperatorTourWarningDto(
                    TourItineraryWarningCodes.DuplicatePoi, repeat.DayNo, repeat.SequenceNo, repeat.PoiId));
            }
        }

        if (firstDepartureLocalDate is null) return warnings;

        foreach (var item in ordered.Where(item => item.PlannedTime.HasValue))
        {
            var date = firstDepartureLocalDate.Value.AddDays(item.DayNo - 1);
            byte dayOfWeek = (byte)date.DayOfWeek;
            var hours = openingHours.FirstOrDefault(candidate =>
                candidate.PoiId == item.PoiId && candidate.DayOfWeek == dayOfWeek);
            if (hours is null)
            {
                warnings.Add(new OperatorTourWarningDto(
                    TourItineraryWarningCodes.OpeningHoursUnknown, item.DayNo, item.SequenceNo, item.PoiId));
                continue;
            }

            if (hours.IsClosed)
            {
                warnings.Add(new OperatorTourWarningDto(
                    TourItineraryWarningCodes.PoiClosedOnDay, item.DayNo, item.SequenceNo, item.PoiId));
                continue;
            }

            var start = item.PlannedTime!.Value.ToTimeSpan();
            var end = start + TimeSpan.FromMinutes(item.StayDurationMinutes);
            if (hours.OpenTime is null || hours.CloseTime is null) continue;
            if (start < hours.OpenTime.Value.ToTimeSpan() || end > hours.CloseTime.Value.ToTimeSpan())
            {
                warnings.Add(new OperatorTourWarningDto(
                    TourItineraryWarningCodes.OutsideOpeningHours, item.DayNo, item.SequenceNo, item.PoiId));
            }
        }

        return warnings;
    }
}