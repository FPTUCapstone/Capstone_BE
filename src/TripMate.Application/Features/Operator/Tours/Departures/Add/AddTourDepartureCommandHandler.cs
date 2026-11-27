using System.Security.Cryptography;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Departures.Add;

public sealed class AddTourDepartureCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<AddTourDepartureCommand, Result<AddTourDepartureResponse>>
{
    private const int MaxAttempts = 5;

    public async Task<Result<AddTourDepartureResponse>> Handle(
        AddTourDepartureCommand request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return AccessDenied();
        if (request.IdempotencyKey == Guid.Empty)
        {
            return Result.Failure<AddTourDepartureResponse>(
                OperatorTourErrorCodes.IdempotencyKeyRequired,
                "An Idempotency-Key is required to add a tour departure.");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AddAttemptAsync(request, currentUser.UserId.Value, cancellationToken);
            }
            catch (Exception exception) when (attempt < MaxAttempts
                && (exception is DbUpdateException || dbContext.IsDeadlock(exception)))
            {
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<Result<AddTourDepartureResponse>> AddAttemptAsync(
        AddTourDepartureCommand request,
        long operatorId,
        CancellationToken cancellationToken)
    {
        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            bool isEligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
                profile.UserId == operatorId
                && profile.ApprovalStatus == OperatorApprovalStatus.Approved
                && profile.User.Role == UserRole.TourOperator
                && profile.User.Status == AccountStatus.Active,
                transactionCancellationToken);
            if (!isEligible) return AccessDenied();

            string fingerprint = ComputeFingerprint(request);
            TourDepartureCreateOperation? existingOperation = await dbContext.TourDepartureCreateOperations
                .AsNoTracking()
                .SingleOrDefaultAsync(operation =>
                    operation.ActorUserId == operatorId
                    && operation.TourId == request.TourId
                    && operation.IdempotencyKey == request.IdempotencyKey,
                    transactionCancellationToken);
            if (existingOperation is not null)
            {
                if (!string.Equals(existingOperation.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return Result.Failure<AddTourDepartureResponse>(
                        OperatorTourErrorCodes.IdempotencyKeyPayloadMismatch,
                        "The Idempotency-Key was already used with different departure data.");
                }

                if (existingOperation.ScheduleId.HasValue)
                {
                    TourSchedule? existingSchedule = await dbContext.TourSchedules
                        .AsNoTracking()
                        .Include(candidate => candidate.Tour)
                        .SingleOrDefaultAsync(candidate => candidate.Id == existingOperation.ScheduleId.Value,
                            transactionCancellationToken);
                    if (existingSchedule is not null)
                    {
                        return Result.Success(ToResponse(
                            existingSchedule,
                            existingSchedule.AdultPrice ?? existingSchedule.Tour.BasePrice,
                            isReplay: true));
                    }
                }

                return Result.Failure<AddTourDepartureResponse>(
                    OperatorTourErrorCodes.IdempotencyKeyPayloadMismatch,
                    "The previous departure request did not complete.");
            }

            Tour? tour = await dbContext.Tours
                .Include(candidate => candidate.ItineraryItems)
                .SingleOrDefaultAsync(candidate =>
                    candidate.Id == request.TourId && candidate.OperatorUserId == operatorId,
                    transactionCancellationToken);
            if (tour is null)
            {
                return Result.Failure<AddTourDepartureResponse>(
                    OperatorTourErrorCodes.TourNotFound,
                    "The tour is unavailable.");
            }

            DateTimeOffset now = clock.UtcNow.ToUniversalTime();
            if (!tour.CanAcceptAdditionalDeparture(now))
            {
                return Result.Failure<AddTourDepartureResponse>(
                    OperatorTourErrorCodes.InvalidState,
                    "Only an approved and published tour can accept additional departures.");
            }

            DateTimeOffset normalizedStart = request.StartAtUtc.ToUniversalTime();
            bool duplicateStart = await dbContext.TourSchedules.AsNoTracking().AnyAsync(schedule =>
                schedule.TourId == tour.Id
                && schedule.Status == TourScheduleStatus.Scheduled
                && schedule.StartAtUtc == normalizedStart,
                transactionCancellationToken);
            if (duplicateStart)
            {
                return Result.Failure<AddTourDepartureResponse>(
                    OperatorTourErrorCodes.DuplicateDeparture,
                    "A scheduled departure with the same start date and time already exists for this tour.");
            }

            var stops = tour.ItineraryItems
                .OrderBy(item => item.DayNo)
                .ThenBy(item => item.SequenceNo)
                .Select(item => new BookedTourStopInput(
                    item.DayNo,
                    item.SequenceNo,
                    item.PoiId,
                    item.PlannedTime,
                    item.StayDurationMinutes,
                    item.Notes))
                .ToArray();
            BookedTourPlanResult plan = BookedTourItineraryTimeline.Plan(
                stops, tour.TimeZoneId, request.StartAtUtc, request.EndAtUtc);
            if (plan.Stops is null)
            {
                return Result.Failure<AddTourDepartureResponse>(
                    OperatorTourErrorCodes.DepartureItineraryMismatch,
                    DescribeItineraryMismatch(plan.FailureCode));
            }

            decimal effectiveAdultPrice = request.AdultPrice ?? tour.BasePrice;
            TourSchedule newSchedule = TourSchedule.Create(
                request.StartAtUtc,
                request.EndAtUtc,
                request.MeetingPoint,
                request.TotalCapacity,
                now,
                request.MinimumCapacity,
                request.RegistrationDeadlineAtUtc,
                effectiveAdultPrice,
                request.ChildPrice,
                request.InfantPrice,
                request.MeetingLatitude,
                request.MeetingLongitude);

            tour.AddDeparture(newSchedule, now);
            dbContext.TourDepartureCreateOperations.Add(TourDepartureCreateOperation.Create(
                operatorId, tour, request.IdempotencyKey, fingerprint, newSchedule, now));
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                operatorId,
                AuditActionTypes.TourDepartureAdded,
                AuditEntityTypes.Tour,
                tour.Id,
                now,
                AuditOutcome.Success,
                afterData: JsonSerializer.Serialize(new
                {
                    TourId = tour.Id,
                    StartAtUtc = newSchedule.StartAtUtc,
                    EndAtUtc = newSchedule.EndAtUtc,
                    TotalCapacity = newSchedule.TotalCapacity,
                    MinimumCapacity = newSchedule.MinimumCapacity,
                    AdultPrice = newSchedule.AdultPrice,
                    ChildPrice = newSchedule.ChildPrice,
                    InfantPrice = newSchedule.InfantPrice,
                })));

            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(ToResponse(newSchedule, effectiveAdultPrice, isReplay: false));
        }, cancellationToken);
    }

    private static AddTourDepartureResponse ToResponse(
        TourSchedule schedule,
        decimal adultPrice,
        bool isReplay) =>
        new(
            schedule.TourId,
            schedule.Id,
            schedule.Status.ToString(),
            schedule.StartAtUtc,
            schedule.EndAtUtc,
            schedule.MeetingPoint,
            schedule.TotalCapacity,
            schedule.MinimumCapacity,
            schedule.ReservedCapacity,
            schedule.RegistrationDeadlineAtUtc,
            adultPrice,
            schedule.ChildPrice,
            schedule.InfantPrice,
            schedule.MeetingLatitude,
            schedule.MeetingLongitude,
            isReplay);

    private static string DescribeItineraryMismatch(string? failureCode) => failureCode switch
    {
        BookedTourItineraryTimeline.StopOutsideDepartureWindowFailureCode =>
            "The departure window does not cover all itinerary days and activity times.",
        BookedTourItineraryTimeline.MissingPlannedTimeFailureCode =>
            "All itinerary activities must have planned start times before adding a bookable departure.",
        BookedTourItineraryTimeline.OverlappingStopsFailureCode =>
            "The tour itinerary contains overlapping activity times that prevent scheduling this departure.",
        BookedTourItineraryTimeline.NoStopsFailureCode =>
            "The tour must have at least one itinerary stop before adding a departure.",
        _ => "The departure schedule is incompatible with the tour itinerary timeline.",
    };

    private static string ComputeFingerprint(AddTourDepartureCommand request)
    {
        byte[] canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.TourId,
            StartAtUtc = request.StartAtUtc.ToUniversalTime(),
            EndAtUtc = request.EndAtUtc.ToUniversalTime(),
            MeetingPoint = OperatorTourValidation.NormalizeText(request.MeetingPoint),
            request.TotalCapacity,
            request.MinimumCapacity,
            RegistrationDeadlineAtUtc = request.RegistrationDeadlineAtUtc?.ToUniversalTime(),
            request.AdultPrice,
            request.ChildPrice,
            request.InfantPrice,
            request.MeetingLatitude,
            request.MeetingLongitude,
        });
        return Convert.ToHexString(SHA256.HashData(canonicalPayload));
    }

    private static Result<AddTourDepartureResponse> AccessDenied() =>
        Result.Failure<AddTourDepartureResponse>(
            OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}