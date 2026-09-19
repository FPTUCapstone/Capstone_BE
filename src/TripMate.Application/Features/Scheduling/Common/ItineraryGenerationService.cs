using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

public sealed class ItineraryGenerationService(
    IRouteDurationProvider routeDurationProvider,
    SchedulingGenerationOptions? options = null)
{
    private const string ConstraintsInfeasible = "planning.constraints_infeasible";
    private const int AutoRestThresholdMinutes = 150;
    private const int RestDurationMinutes = 30;
    private readonly SchedulingGenerationOptions _options = options ?? new SchedulingGenerationOptions();

    public async Task<Result<GeneratedItineraryPlan>> GenerateAsync(
        GenerationInput input,
        CancellationToken cancellationToken)
    {
        if (input.AvailableMinutes is < 60 or > 720
            || input.BudgetVnd is < 0
            || input.MandatoryPoiIds.Count > 6
            || input.MandatoryPoiIds.Distinct().Count() != input.MandatoryPoiIds.Count)
        {
            return Infeasible("The itinerary request is invalid.");
        }

        var candidatesById = input.Candidates.ToDictionary(candidate => candidate.Id);
        if (candidatesById.Count != input.Candidates.Count
            || input.MandatoryPoiIds.Any(id => !candidatesById.ContainsKey(id)))
        {
            return Infeasible("A mandatory location is unavailable.");
        }

        var mandatoryCandidates = input.MandatoryPoiIds
            .Select(id => candidatesById[id])
            .ToArray();
        if (input.BudgetVnd.HasValue && mandatoryCandidates.Any(candidate =>
                candidate.EstimatedVisitCost is null))
        {
            return Infeasible("A mandatory location has an unknown estimated cost.");
        }

        var routePoints = new List<RoutePoint> { input.Start };
        routePoints.AddRange(input.Candidates.Select(candidate => candidate.Location));
        routePoints.Add(input.End);
        var matrix = await routeDurationProvider.GetMatrixAsync(
            routePoints,
            input.TransportMode,
            cancellationToken);
        if (matrix.PointCount != routePoints.Count)
        {
            throw new InvalidOperationException("Route provider returned a matrix with an unexpected size.");
        }

        GeneratedItineraryPlan? bestPlan = null;
        foreach (var sequence in Permute(mandatoryCandidates))
        {
            var plan = TryBuildPlan(input, sequence, matrix, input.Candidates.ToArray());
            if (plan is not null && (bestPlan is null || plan.TotalDurationMinutes < bestPlan.TotalDurationMinutes))
            {
                bestPlan = plan;
            }
        }

        return bestPlan is null
            ? Infeasible("The mandatory locations cannot fit within the selected time, hours, budget, and end point.")
            : Result.Success(bestPlan);
    }

    private GeneratedItineraryPlan? TryBuildPlan(
        GenerationInput input,
        IReadOnlyList<GenerationCandidate> sequence,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates)
    {
        var currentTime = input.StartAtUtc;
        var continuousMinutes = 0;
        var totalCost = 0m;
        var items = new List<GeneratedItineraryItem>();
        var previousIndex = 0;

        for (var sequenceIndex = 0; sequenceIndex < sequence.Count; sequenceIndex++)
        {
            var candidate = sequence[sequenceIndex];
            var candidateIndex = matrixCandidates
                .Select((matrixCandidate, index) => new { matrixCandidate.Id, Index = index })
                .Single(item => item.Id == candidate.Id)
                .Index + 1;
            var travelMinutes = matrix.GetMinutes(previousIndex, candidateIndex)
                + _options.TransitionBufferMinutes;
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

            totalCost += candidate.EstimatedVisitCost ?? 0m;
            if (input.BudgetVnd.HasValue && totalCost > input.BudgetVnd.Value)
            {
                return null;
            }

            items.Add(new GeneratedItineraryItem(
                items.Count + 1,
                candidate.Id,
                candidate.Name,
                ItineraryItemKind.Visit,
                currentTime,
                departureTime,
                true,
                candidate.EstimatedVisitCost,
                "Mandatory location"));
            continuousMinutes += candidate.VisitDurationMinutes;
            currentTime = departureTime;
            previousIndex = candidateIndex;

            if (NeedsRest(input, continuousMinutes, items.Count(item => item.Kind == ItineraryItemKind.Rest)))
            {
                var nextIndex = sequenceIndex + 1 < sequence.Count
                    ? MatrixCandidateIndex(sequence[sequenceIndex + 1], matrixCandidates)
                    : matrix.PointCount - 1;
                var restCandidate = FindQualifiedRestCandidate(
                    matrixCandidates,
                    input,
                    previousIndex,
                    nextIndex,
                    currentTime,
                    matrix);
                if (restCandidate is not null)
                {
                    var restIndex = MatrixCandidateIndex(restCandidate, matrixCandidates);
                    var restArrival = currentTime.AddMinutes(
                        matrix.GetMinutes(previousIndex, restIndex)
                        + _options.TransitionBufferMinutes);
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

        var optionalCandidates = matrixCandidates
            .Where(candidate => !input.MandatoryPoiIds.Contains(candidate.Id)
                && !IsQualifiedRestCandidate(candidate))
            .OrderByDescending(candidate => candidate.PreferenceScore)
            .ThenByDescending(candidate => candidate.ScenicScore ?? decimal.MinValue)
            .ThenByDescending(candidate => candidate.PhotoRating ?? decimal.MinValue)
            .ThenBy(candidate => MatrixMinutesFromStart(candidate, matrixCandidates, matrix))
            .ThenBy(candidate => candidate.EstimatedVisitCost ?? decimal.MaxValue)
            .ThenBy(candidate => candidate.Id)
            .ToArray();

        foreach (var candidate in optionalCandidates)
        {
            if (input.BudgetVnd.HasValue && candidate.EstimatedVisitCost is null)
            {
                continue;
            }

            var candidateIndex = matrixCandidates
                .Select((matrixCandidate, index) => new { matrixCandidate.Id, Index = index })
                .Single(item => item.Id == candidate.Id)
                .Index + 1;
            var travelMinutes = matrix.GetMinutes(previousIndex, candidateIndex)
                + _options.TransitionBufferMinutes;
            var arrivalTime = currentTime.AddMinutes(
                travelMinutes);
            arrivalTime = AlignToOpeningHours(arrivalTime, candidate, input.TimeZone);
            if (arrivalTime == DateTimeOffset.MinValue)
            {
                continue;
            }

            var departureTime = arrivalTime.AddMinutes(candidate.VisitDurationMinutes);
            var proposedCost = totalCost + (candidate.EstimatedVisitCost ?? 0m);
            var continuousMinutesAfterVisit = continuousMinutes
                + travelMinutes
                + candidate.VisitDurationMinutes;
            var nextCandidate = optionalCandidates
                .SkipWhile(optionalCandidate => optionalCandidate.Id != candidate.Id)
                .Skip(1)
                .FirstOrDefault();
            var nextIndex = nextCandidate is null
                ? matrix.PointCount - 1
                : MatrixCandidateIndex(nextCandidate, matrixCandidates);
            var restInsertion = NeedsRest(
                    input,
                    continuousMinutesAfterVisit,
                    items.Count(item => item.Kind == ItineraryItemKind.Rest))
                ? CreateRestInsertion(
                    input,
                    matrixCandidates,
                    previousIndex: candidateIndex,
                    nextIndex: nextIndex,
                    currentTime: departureTime,
                    matrix: matrix)
                : null;
            var endTime = restInsertion is null
                ? departureTime.AddMinutes(
                    matrix.GetMinutes(candidateIndex, matrix.PointCount - 1)
                    + _options.FinalReturnBufferMinutes)
                : restInsertion.Value.EndAtUtc.AddMinutes(
                    matrix.GetMinutes(restInsertion.Value.PreviousIndex, matrix.PointCount - 1)
                    + _options.FinalReturnBufferMinutes);
            if (!FitsOpeningHours(arrivalTime, departureTime, candidate, input.TimeZone)
                || (input.BudgetVnd.HasValue && proposedCost > input.BudgetVnd.Value)
                || endTime > input.StartAtUtc.AddMinutes(input.AvailableMinutes))
            {
                continue;
            }

            items.Add(new GeneratedItineraryItem(
                items.Count + 1,
                candidate.Id,
                candidate.Name,
                ItineraryItemKind.Visit,
                arrivalTime,
                departureTime,
                false,
                candidate.EstimatedVisitCost,
                "Suggested nearby location"));
            totalCost = proposedCost;
            currentTime = restInsertion?.EndAtUtc ?? departureTime;
            previousIndex = restInsertion?.PreviousIndex ?? candidateIndex;
            continuousMinutes = restInsertion is null ? continuousMinutesAfterVisit : 0;
            if (restInsertion is not null)
            {
                items.Add(restInsertion.Value.Item with { SequenceNo = items.Count + 1 });
            }
        }

        if (items.All(item => item.Kind != ItineraryItemKind.Visit))
        {
            return null;
        }

        currentTime = currentTime.AddMinutes(
            matrix.GetMinutes(previousIndex, matrix.PointCount - 1)
            + _options.FinalReturnBufferMinutes);
        if (currentTime > input.StartAtUtc.AddMinutes(input.AvailableMinutes))
        {
            return null;
        }

        return new GeneratedItineraryPlan(
            items,
            currentTime,
            (int)Math.Ceiling((currentTime - input.StartAtUtc).TotalMinutes),
            totalCost);
    }

    private static bool NeedsRest(GenerationInput input, int continuousMinutes, int restCount) =>
        input.RestPreference switch
        {
            RestPreference.Auto => input.AvailableMinutes >= 300
                && restCount == 0
                && continuousMinutes >= AutoRestThresholdMinutes,
            RestPreference.Frequent => continuousMinutes >= 120,
            _ => false,
        };

    private static int MatrixMinutesFromStart(
        GenerationCandidate candidate,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        RouteDurationMatrix matrix)
    {
        var candidateIndex = matrixCandidates
            .Select((matrixCandidate, index) => new { matrixCandidate.Id, Index = index })
            .Single(item => item.Id == candidate.Id)
            .Index + 1;
        return matrix.GetMinutes(0, candidateIndex);
    }

    private GenerationCandidate? FindQualifiedRestCandidate(
        IReadOnlyList<GenerationCandidate> candidates,
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
                MatrixCandidateIndex(candidate, candidates)))
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault(candidate =>
            {
                var restIndex = MatrixCandidateIndex(candidate, candidates);
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

    private (GeneratedItineraryItem Item, DateTimeOffset EndAtUtc, int PreviousIndex)? CreateRestInsertion(
        GenerationInput input,
        IReadOnlyList<GenerationCandidate> candidates,
        int previousIndex,
        int nextIndex,
        DateTimeOffset currentTime,
        RouteDurationMatrix matrix)
    {
        var restCandidate = FindQualifiedRestCandidate(
            candidates,
            input,
            previousIndex,
            nextIndex,
            currentTime,
            matrix);
        if (restCandidate is not null)
        {
            var restIndex = MatrixCandidateIndex(restCandidate, candidates);
            var restArrival = currentTime.AddMinutes(
                matrix.GetMinutes(previousIndex, restIndex)
                + _options.TransitionBufferMinutes);
            var restDeparture = restArrival.AddMinutes(RestDurationMinutes);
            return (
                new GeneratedItineraryItem(
                    0,
                    restCandidate.Id,
                    restCandidate.Name,
                    ItineraryItemKind.Rest,
                    restArrival,
                    restDeparture,
                    false,
                    null,
                    "Suggested rest stop"),
                restDeparture,
                restIndex);
        }

        var restEnd = currentTime.AddMinutes(RestDurationMinutes);
        return (
            new GeneratedItineraryItem(
                0,
                null,
                null,
                ItineraryItemKind.Rest,
                currentTime,
                restEnd,
                false,
                null,
                "Free/rest time"),
            restEnd,
            previousIndex);
    }

    private static bool IsQualifiedRestCandidate(GenerationCandidate candidate) =>
        candidate.CategoryName is not null
        && IsRestCategory(candidate.CategoryName, candidate.HasShelter);

    private static bool IsRestCategory(string categoryName, bool hasShelter)
    {
        var normalizedCategory = new string(categoryName
            .Trim()
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
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

    private static int MatrixCandidateIndex(
        GenerationCandidate candidate,
        IReadOnlyList<GenerationCandidate> candidates) =>
        candidates
            .Select((matrixCandidate, index) => new { matrixCandidate.Id, Index = index })
            .Single(item => item.Id == candidate.Id)
            .Index + 1;

    private static DateTimeOffset AlignToOpeningHours(
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

    private static bool FitsOpeningHours(
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

    private static IEnumerable<IReadOnlyList<GenerationCandidate>> Permute(
        IReadOnlyList<GenerationCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            yield return [];
            yield break;
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var remaining = candidates.Where((_, candidateIndex) => candidateIndex != index).ToArray();
            foreach (var suffix in Permute(remaining))
            {
                yield return new[] { candidate }.Concat(suffix).ToArray();
            }
        }
    }

    private static Result<GeneratedItineraryPlan> Infeasible(string message) =>
        Result.Failure<GeneratedItineraryPlan>(ConstraintsInfeasible, message);
}
