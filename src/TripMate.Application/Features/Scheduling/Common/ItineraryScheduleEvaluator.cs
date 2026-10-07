using System.Globalization;
using System.Text;

using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

public sealed record EvaluatedItinerarySchedule(
    GeneratedItineraryPlan Plan,
    int TotalMatrixTravelMinutes,
    int TotalDurationMinutes,
    DateTimeOffset EndAtUtc,
    decimal TotalEstimatedCost,
    IReadOnlyList<long> VisitPoiIds);

public sealed class ItineraryScheduleEvaluator(SchedulingGenerationOptions? options = null)
{
    private const int AutoRestThresholdMinutes = 150;
    private const int RestDurationMinutes = 30;
    private readonly SchedulingGenerationOptions _options = options ?? new SchedulingGenerationOptions();

    public EvaluatedItinerarySchedule? Evaluate(
        GenerationInput input,
        IReadOnlyList<GenerationCandidate> sequence,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (sequence.Count == 0)
        {
            return null;
        }

        var currentTime = input.StartAtUtc;
        var continuousMinutes = 0;
        var totalCost = 0m;
        var totalMatrixTravelMinutes = 0;
        var items = new List<GeneratedItineraryItem>(sequence.Count * 2);
        var previousIndex = 0;

        for (var sequenceIndex = 0; sequenceIndex < sequence.Count; sequenceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = sequence[sequenceIndex];
            var candidateIndex = candidateMatrixIndices[candidate.Id];

            var matrixLegTravel = matrix.GetMinutes(previousIndex, candidateIndex);
            totalMatrixTravelMinutes += matrixLegTravel;
            var travelMinutes = matrixLegTravel + _options.TransitionBufferMinutes;
            currentTime = currentTime.AddMinutes(travelMinutes);
            continuousMinutes += travelMinutes;

            currentTime = AlignToOpeningHours(currentTime, candidate, input.TimeZone);
            if (currentTime == DateTimeOffset.MinValue)
            {
                return null;
            }

            var departureTime = currentTime.AddMinutes(candidate.VisitDurationMinutes);
            if (!FitsOpeningHours(currentTime, departureTime, candidate, input.TimeZone))
            {
                return null;
            }

            if (input.BudgetVnd.HasValue && candidate.EstimatedVisitCost is null)
            {
                return null;
            }

            totalCost += candidate.EstimatedVisitCost ?? 0m;
            if (input.BudgetVnd.HasValue && totalCost > input.BudgetVnd.Value)
            {
                return null;
            }

            var isMandatoryStop = input.MandatoryPoiIds.Contains(candidate.Id);
            items.Add(new GeneratedItineraryItem(
                items.Count + 1,
                candidate.Id,
                candidate.Name,
                ItineraryItemKind.Visit,
                currentTime,
                departureTime,
                isMandatoryStop,
                candidate.EstimatedVisitCost,
                isMandatoryStop ? "Mandatory location" : "Suggested nearby location"));

            continuousMinutes += candidate.VisitDurationMinutes;
            currentTime = departureTime;
            previousIndex = candidateIndex;

            if (NeedsRest(input, continuousMinutes, items.Count(item => item.Kind == ItineraryItemKind.Rest)))
            {
                var nextIndex = sequenceIndex + 1 < sequence.Count
                    ? candidateMatrixIndices[sequence[sequenceIndex + 1].Id]
                    : matrix.PointCount - 1;

                var restCandidate = FindQualifiedRestCandidate(
                    matrixCandidates,
                    candidateMatrixIndices,
                    input,
                    previousIndex,
                    nextIndex,
                    currentTime,
                    matrix);

                if (restCandidate is not null)
                {
                    var restIndex = candidateMatrixIndices[restCandidate.Id];
                    var restLegTravel = matrix.GetMinutes(previousIndex, restIndex);
                    totalMatrixTravelMinutes += restLegTravel;
                    var restArrival = currentTime.AddMinutes(restLegTravel + _options.TransitionBufferMinutes);
                    var restDeparture = restArrival.AddMinutes(RestDurationMinutes);

                    items.Add(new GeneratedItineraryItem(
                        items.Count + 1,
                        restCandidate.Id,
                        restCandidate.Name,
                        ItineraryItemKind.Rest,
                        restArrival,
                        restDeparture,
                        false,
                        null,
                        "Suggested rest stop"));

                    currentTime = restDeparture;
                    previousIndex = restIndex;
                }
                else
                {
                    var restEnd = currentTime.AddMinutes(RestDurationMinutes);
                    items.Add(new GeneratedItineraryItem(
                        items.Count + 1,
                        null,
                        null,
                        ItineraryItemKind.Rest,
                        currentTime,
                        restEnd,
                        false,
                        null,
                        "Free/rest time"));

                    currentTime = restEnd;
                }

                continuousMinutes = 0;
            }
        }

        if (items.All(item => item.Kind != ItineraryItemKind.Visit))
        {
            return null;
        }

        var finalLegTravel = matrix.GetMinutes(previousIndex, matrix.PointCount - 1);
        totalMatrixTravelMinutes += finalLegTravel;
        var finalArrivalTime = currentTime.AddMinutes(finalLegTravel + _options.FinalReturnBufferMinutes);

        if (finalArrivalTime > input.StartAtUtc.AddMinutes(input.AvailableMinutes))
        {
            return null;
        }

        var itemsWithTravelDurations = AddTravelDurations(items, matrix, candidateMatrixIndices);
        var totalDurationMinutes = (int)Math.Ceiling((finalArrivalTime - input.StartAtUtc).TotalMinutes);

        var plan = new GeneratedItineraryPlan(
            itemsWithTravelDurations,
            finalArrivalTime,
            totalDurationMinutes,
            totalCost);

        var visitPoiIds = sequence.Select(c => c.Id).ToArray();

        return new EvaluatedItinerarySchedule(
            plan,
            totalMatrixTravelMinutes,
            totalDurationMinutes,
            finalArrivalTime,
            totalCost,
            visitPoiIds);
    }

    private static IReadOnlyCollection<GeneratedItineraryItem> AddTravelDurations(
        IReadOnlyList<GeneratedItineraryItem> items,
        RouteDurationMatrix matrix,
        IReadOnlyDictionary<long, int> candidateMatrixIndices)
    {
        var locationIndices = new int[items.Count];
        var previousIndex = 0;
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].PointOfInterestId is { } poiId)
            {
                previousIndex = candidateMatrixIndices[poiId];
            }

            locationIndices[index] = previousIndex;
        }

        return items
            .Select((item, index) => item with
            {
                TravelDurationToNextMinutes = index == items.Count - 1
                    ? null
                    : matrix.GetMinutes(locationIndices[index], locationIndices[index + 1]),
            })
            .ToArray();
    }

    public static bool NeedsRest(GenerationInput input, int continuousMinutes, int restCount) =>
        input.RestPreference switch
        {
            RestPreference.Auto => input.AvailableMinutes >= 300
                && restCount == 0
                && continuousMinutes >= AutoRestThresholdMinutes,
            RestPreference.Frequent => continuousMinutes >= 120,
            _ => false,
        };

    public GenerationCandidate? FindQualifiedRestCandidate(
        IReadOnlyList<GenerationCandidate> candidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        GenerationInput input,
        int previousIndex,
        int nextIndex,
        DateTimeOffset currentTime,
        RouteDurationMatrix matrix)
    {
        return candidates
            .Where(candidate =>
                !input.MandatoryPoiIds.Contains(candidate.Id)
                && IsQualifiedRestCandidate(candidate))
            .OrderBy(candidate => matrix.GetMinutes(
                previousIndex,
                candidateMatrixIndices[candidate.Id]))
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault(candidate =>
            {
                var restIndex = candidateMatrixIndices[candidate.Id];
                var restArrival = currentTime.AddMinutes(
                    matrix.GetMinutes(previousIndex, restIndex)
                    + _options.TransitionBufferMinutes);
                var restDeparture = restArrival.AddMinutes(RestDurationMinutes);
                var nextArrival = restDeparture.AddMinutes(
                    matrix.GetMinutes(restIndex, nextIndex)
                    + (nextIndex == matrix.PointCount - 1
                        ? _options.FinalReturnBufferMinutes
                        : _options.TransitionBufferMinutes));
                return FitsOpeningHours(
                        restArrival,
                        restDeparture,
                        candidate,
                        input.TimeZone)
                    && nextArrival <= input.StartAtUtc.AddMinutes(input.AvailableMinutes);
            });
    }

    public static bool IsQualifiedRestCandidate(GenerationCandidate candidate) =>
        candidate.CategoryName is not null
        && IsRestCategory(candidate.CategoryName, candidate.HasShelter);

    public static bool IsRestCategory(string categoryName, bool hasShelter)
    {
        var normalizedCategory = new string(categoryName
            .Trim()
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character)
                != UnicodeCategory.NonSpacingMark)
            .ToArray())
            .ToLowerInvariant();

        if (normalizedCategory is "cafe" or "coffee shop" or "restaurant" or "rest area" or "rest stop")
        {
            return true;
        }

        return hasShelter
            && normalizedCategory is ("beach"
                or "natural attraction"
                or "park"
                or "theme park"
                or "water park");
    }

    public static DateTimeOffset AlignToOpeningHours(
        DateTimeOffset arrivalUtc,
        GenerationCandidate candidate,
        TimeZoneInfo timeZone)
    {
        var localArrival = TimeZoneInfo.ConvertTime(arrivalUtc, timeZone);
        var opening = candidate.OpeningHours.SingleOrDefault(hours =>
            hours.DayOfWeek == (byte)localArrival.DayOfWeek);
        if (opening is null || localArrival.TimeOfDay > opening.CloseTime.ToTimeSpan())
        {
            return DateTimeOffset.MinValue;
        }

        return localArrival.TimeOfDay < opening.OpenTime.ToTimeSpan()
            ? TimeZoneInfo.ConvertTimeToUtc(
                localArrival.Date.Add(opening.OpenTime.ToTimeSpan()),
                timeZone)
            : arrivalUtc;
    }

    public static bool FitsOpeningHours(
        DateTimeOffset arrivalUtc,
        DateTimeOffset departureUtc,
        GenerationCandidate candidate,
        TimeZoneInfo timeZone)
    {
        var localArrival = TimeZoneInfo.ConvertTime(arrivalUtc, timeZone);
        var localDeparture = TimeZoneInfo.ConvertTime(departureUtc, timeZone);
        var opening = candidate.OpeningHours.SingleOrDefault(hours =>
            hours.DayOfWeek == (byte)localArrival.DayOfWeek);

        return opening is not null
            && localArrival.Date == localDeparture.Date
            && localArrival.TimeOfDay >= opening.OpenTime.ToTimeSpan()
            && localDeparture.TimeOfDay <= opening.CloseTime.ToTimeSpan();
    }
}