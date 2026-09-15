using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Create;

public sealed class CreateSchedulingRequestCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    IRouteDurationProvider routeDurationProvider,
    ISchedulingRequestLock schedulingRequestLock,
    SchedulingGenerationOptions? generationOptions = null)
    : IRequestHandler<CreateSchedulingRequestCommand, Result<SchedulingResponseDto>>
{
    private const int DailySuccessfulGenerationLimit = 3;

    public async Task<Result<SchedulingResponseDto>> Handle(
        CreateSchedulingRequestCommand command,
        CancellationToken cancellationToken)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(command.TimeZoneId.Trim());
        var startAtUtc = command.StartAt.ToUniversalTime();
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startAtUtc, timeZone).Date);
        var requestHash = ComputeRequestHash(command, startAtUtc);

        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            await schedulingRequestLock.AcquireAsync(
                command.TravelerUserId,
                localDate,
                command.IdempotencyKey,
                transactionCancellationToken);

            var previousRequest = await dbContext.SchedulingRequests
                .SingleOrDefaultAsync(request =>
                    request.TravelerUserId == command.TravelerUserId
                    && request.IdempotencyKey == command.IdempotencyKey,
                    transactionCancellationToken);
            if (previousRequest is not null)
            {
                return await ReplayAsync(previousRequest, requestHash, transactionCancellationToken);
            }

            var localDayCompletedCount = await CountSuccessfulRequestsForLocalDayAsync(
                command.TravelerUserId,
                localDate,
                timeZone,
                transactionCancellationToken);
            if (localDayCompletedCount >= DailySuccessfulGenerationLimit)
            {
                return Result.Failure<SchedulingResponseDto>(
                    SchedulingErrorCodes.DailyGenerationLimitReached,
                    "You can generate up to 3 itineraries per day. Please try again tomorrow.");
            }

            var activePois = await dbContext.PointsOfInterest
                .AsNoTracking()
                .Include(poi => poi.OpeningHours)
                .Where(poi => poi.Status == PointOfInterestStatus.Active)
                .ToListAsync(transactionCancellationToken);
            var endPoi = command.EndPoiId.HasValue
                ? activePois.SingleOrDefault(poi => poi.Id == command.EndPoiId.Value)
                : null;
            if (command.EndPoiId.HasValue && endPoi is null)
            {
                return Infeasible("The selected ending location is unavailable.");
            }

            var selectablePois = activePois
                .Where(IsPlanningReady)
                .Where(poi => DistanceInKilometers(
                    command.ExplorationLatitude,
                    command.ExplorationLongitude,
                    poi.Latitude,
                    poi.Longitude) <= command.SearchRadiusKm)
                .ToArray();
            var selectableIds = selectablePois.Select(poi => poi.Id).ToHashSet();
            if (command.MandatoryPoiIds.Any(id => !selectableIds.Contains(id)))
            {
                return Infeasible("A mandatory location is unavailable or outside the selected area.");
            }

            if (selectablePois.Length == 0)
            {
                return Infeasible("No selectable locations were found in the selected area.");
            }

            if (command.TransportMode == TransportMode.PublicTransit)
            {
                return Infeasible("Public transit routing is not available yet. Choose walking, motorbike, or car.");
            }

            var now = dateTimeProvider.UtcNow;
            var mandatoryIdsJson = JsonSerializer.Serialize(command.MandatoryPoiIds.Order());
            var schedulingRequest = SchedulingRequest.Create(
                command.TravelerUserId,
                command.IdempotencyKey,
                requestHash,
                startAtUtc,
                command.TimeZoneId,
                command.StartLatitude,
                command.StartLongitude,
                command.ExplorationLatitude,
                command.ExplorationLongitude,
                command.EndPoiId,
                command.ReturnToStart,
                command.AvailableMinutes,
                command.TransportMode,
                command.SearchRadiusKm,
                command.BudgetVnd,
                mandatoryIdsJson,
                command.RestPreference,
                now);
            dbContext.SchedulingRequests.Add(schedulingRequest);

            var end = endPoi is null
                ? new RoutePoint(command.StartLatitude, command.StartLongitude)
                : new RoutePoint(endPoi.Latitude, endPoi.Longitude);
            var input = new GenerationInput(
                startAtUtc,
                timeZone,
                new RoutePoint(command.StartLatitude, command.StartLongitude),
                end,
                command.AvailableMinutes,
                command.TransportMode,
                command.RestPreference,
                command.BudgetVnd,
                selectablePois.Select(ToCandidate).ToArray(),
                command.MandatoryPoiIds);
            var plan = await new ItineraryGenerationService(routeDurationProvider, generationOptions)
                .GenerateAsync(input, transactionCancellationToken);
            if (plan.IsFailure)
            {
                schedulingRequest.FailInfeasible(SchedulingErrorCodes.ConstraintsInfeasible, now);
                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return Result.Failure<SchedulingResponseDto>(
                    SchedulingErrorCodes.ConstraintsInfeasible,
                    plan.ErrorMessage ?? "The selected constraints cannot produce an itinerary.");
            }

            var itinerary = Itinerary.CreateCspGenerated(
                schedulingRequest,
                $"Generated itinerary - {TimeZoneInfo.ConvertTime(startAtUtc, timeZone):dd MMM yyyy}",
                startAtUtc,
                plan.Value.EndAtUtc);
            foreach (var item in plan.Value.Items)
            {
                itinerary.AddItem(item.Kind == ItineraryItemKind.Rest
                    ? ItineraryItem.CreateRest(
                        item.SequenceNo,
                        item.PlannedArrivalUtc,
                        item.PlannedDepartureUtc,
                        item.RecommendationReason)
                    : ItineraryItem.CreateVisit(
                        item.SequenceNo,
                        item.PointOfInterestId!.Value,
                        item.PlannedArrivalUtc,
                        item.PlannedDepartureUtc,
                        item.IsMandatory,
                        item.EstimatedCost,
                        item.RecommendationReason));
            }

            schedulingRequest.Complete(now);
            dbContext.Itineraries.Add(itinerary);
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(ToResponse(schedulingRequest, itinerary, plan.Value));
        }, cancellationToken);
    }

    private async Task<Result<SchedulingResponseDto>> ReplayAsync(
        SchedulingRequest previousRequest,
        string requestHash,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(previousRequest.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return Result.Failure<SchedulingResponseDto>(
                SchedulingErrorCodes.IdempotencyKeyPayloadMismatch,
                "The Idempotency-Key was already used with different request data.");
        }

        if (previousRequest.Status == SchedulingRequestStatus.Failed)
        {
            return Result.Failure<SchedulingResponseDto>(
                previousRequest.FailureCode ?? SchedulingErrorCodes.ConstraintsInfeasible,
                "The selected constraints cannot produce an itinerary.");
        }

        var itinerary = await dbContext.Itineraries
            .AsNoTracking()
            .Include(item => item.Items)
            .ThenInclude(item => item.PointOfInterest)
            .SingleAsync(item => item.SchedulingRequestId == previousRequest.Id, cancellationToken);
        return Result.Success(ToResponse(previousRequest, itinerary));
    }

    private async Task<int> CountSuccessfulRequestsForLocalDayAsync(
        long travelerUserId,
        DateOnly localDate,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var completedRequestTimes = await dbContext.SchedulingRequests
            .Where(request => request.TravelerUserId == travelerUserId
                && request.Status == SchedulingRequestStatus.Completed)
            .Select(request => request.RequestedAtUtc)
            .ToListAsync(cancellationToken);

        return completedRequestTimes.Count(requestedAt =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(requestedAt, timeZone).Date) == localDate);
    }

    private static Result<SchedulingResponseDto> Infeasible(string message) =>
        Result.Failure<SchedulingResponseDto>(SchedulingErrorCodes.ConstraintsInfeasible, message);

    private static bool IsPlanningReady(PointOfInterest poi) =>
        poi.SourceUrl is not null
        && poi.VerifiedAtUtc.HasValue
        && poi.OpeningHours.Any(hours => !hours.IsClosed && hours.OpenTime.HasValue && hours.CloseTime.HasValue);

    private static GenerationCandidate ToCandidate(PointOfInterest poi) =>
        new(
            poi.Id,
            poi.Name,
            new RoutePoint(poi.Latitude, poi.Longitude),
            poi.AverageVisitDurationMinutes,
            poi.EstimatedVisitCost,
            poi.OpeningHours
                .Where(hours => !hours.IsClosed && hours.OpenTime.HasValue && hours.CloseTime.HasValue)
                .Select(hours => new GenerationOpeningHours(
                    hours.DayOfWeek,
                    hours.OpenTime!.Value,
                    hours.CloseTime!.Value))
                .ToArray());

    private static SchedulingResponseDto ToResponse(
        SchedulingRequest schedulingRequest,
        Itinerary itinerary,
        GeneratedItineraryPlan plan) =>
        new(
            schedulingRequest.Id,
            itinerary.Id,
            itinerary.Title ?? string.Empty,
            itinerary.Status,
            plan.TotalEstimatedCost,
            plan.TotalDurationMinutes,
            plan.Items.Select(item => new SchedulingItemDto(
                item.SequenceNo,
                item.PointOfInterestId,
                item.PointOfInterestName,
                item.Kind,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                (int)(item.PlannedDepartureUtc - item.PlannedArrivalUtc).TotalMinutes,
                null,
                item.EstimatedCost,
                item.IsMandatory,
                item.RecommendationReason)).ToArray());

    private static SchedulingResponseDto ToResponse(SchedulingRequest schedulingRequest, Itinerary itinerary)
    {
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        return new SchedulingResponseDto(
            schedulingRequest.Id,
            itinerary.Id,
            itinerary.Title ?? string.Empty,
            itinerary.Status,
            items.Sum(item => item.EstimatedCost ?? 0m),
            itinerary.ValidFromUtc.HasValue && itinerary.ValidToUtc.HasValue
                ? (int)Math.Ceiling((itinerary.ValidToUtc.Value - itinerary.ValidFromUtc.Value).TotalMinutes)
                : 0,
            items.Select(item => new SchedulingItemDto(
                item.SequenceNo,
                item.PointOfInterestId,
                item.PointOfInterest?.Name,
                item.Kind,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                item.StayDurationMinutes,
                null,
                item.EstimatedCost,
                item.IsMandatory,
                item.RecommendationReason)).ToArray());
    }

    private static string ComputeRequestHash(CreateSchedulingRequestCommand command, DateTimeOffset startAtUtc)
    {
        var payload = string.Join('|',
            startAtUtc.ToString("O", CultureInfo.InvariantCulture),
            command.TimeZoneId.Trim(),
            command.StartLatitude.ToString("G29", CultureInfo.InvariantCulture),
            command.StartLongitude.ToString("G29", CultureInfo.InvariantCulture),
            command.ExplorationLatitude.ToString("G29", CultureInfo.InvariantCulture),
            command.ExplorationLongitude.ToString("G29", CultureInfo.InvariantCulture),
            command.EndPoiId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            command.ReturnToStart,
            command.AvailableMinutes.ToString(CultureInfo.InvariantCulture),
            command.TransportMode,
            command.SearchRadiusKm.ToString("G29", CultureInfo.InvariantCulture),
            command.BudgetVnd?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join(',', command.MandatoryPoiIds.Order()),
            command.RestPreference);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static decimal DistanceInKilometers(
        decimal firstLatitude,
        decimal firstLongitude,
        decimal secondLatitude,
        decimal secondLongitude)
    {
        const double earthRadiusKm = 6371d;
        var latitudeDelta = DegreesToRadians((double)(secondLatitude - firstLatitude));
        var longitudeDelta = DegreesToRadians((double)(secondLongitude - firstLongitude));
        var firstLatitudeRadians = DegreesToRadians((double)firstLatitude);
        var secondLatitudeRadians = DegreesToRadians((double)secondLatitude);
        var haversine = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2)
            + Math.Cos(firstLatitudeRadians) * Math.Cos(secondLatitudeRadians)
            * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return (decimal)(earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1 - haversine)));
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180d;
}
