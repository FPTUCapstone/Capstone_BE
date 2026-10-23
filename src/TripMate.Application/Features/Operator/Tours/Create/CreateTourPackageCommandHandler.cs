using System.Security.Cryptography;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Create;

public sealed class CreateTourPackageCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<CreateTourPackageCommand, Result<CreateTourPackageResponse>>
{
    // Concurrent duplicates (double tap, client retry) all read "no operation yet" under SERIALIZABLE and
    // then collide on the (actor, key) unique index or are chosen as deadlock victims. Re-running the whole
    // attempt makes the loser find the winner's committed operation and replay it instead of failing.
    private const int MaxAttempts = 5;

    public async Task<Result<CreateTourPackageResponse>> Handle(
        CreateTourPackageCommand request, CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return AccessDenied();
        if (request.IdempotencyKey == Guid.Empty)
        {
            return Result.Failure<CreateTourPackageResponse>(
                OperatorTourErrorCodes.IdempotencyKeyRequired,
                "An Idempotency-Key is required to create a tour package.");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CreateAttemptAsync(request, currentUser.UserId.Value, cancellationToken);
            }
            catch (Exception exception) when (attempt < MaxAttempts
                && (exception is DbUpdateException || dbContext.IsDeadlock(exception)))
            {
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<Result<CreateTourPackageResponse>> CreateAttemptAsync(
        CreateTourPackageCommand request, long operatorId, CancellationToken cancellationToken)
    {
        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            var isEligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
                profile.UserId == operatorId &&
                profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
                profile.User.Role == UserRole.TourOperator &&
                profile.User.Status == AccountStatus.Active, transactionCancellationToken);
            if (!isEligible) return AccessDenied();

            string fingerprint = ComputeFingerprint(request);
            var existingOperation = await dbContext.TourPackageCreateOperations
                .SingleOrDefaultAsync(operation =>
                    operation.ActorUserId == operatorId &&
                    operation.IdempotencyKey == request.IdempotencyKey,
                    transactionCancellationToken);
            if (existingOperation is not null)
            {
                if (!string.Equals(existingOperation.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return Result.Failure<CreateTourPackageResponse>(
                        OperatorTourErrorCodes.IdempotencyKeyPayloadMismatch,
                        "The Idempotency-Key was already used with different request data.");
                }

                if (existingOperation.TourId.HasValue)
                {
                    return Result.Success(new CreateTourPackageResponse(
                        existingOperation.TourId.Value,
                        nameof(TourStatus.Draft)));
                }

                return Result.Failure<CreateTourPackageResponse>(
                    OperatorTourErrorCodes.IdempotencyKeyPayloadMismatch,
                    "The previous create request did not complete.");
            }

            if (request.CategoryId.HasValue && !await dbContext.TourCategories.AsNoTracking()
                    .AnyAsync(category => category.Id == request.CategoryId.Value && category.IsActive, transactionCancellationToken))
                return Result.Failure<CreateTourPackageResponse>(OperatorTourErrorCodes.CategoryNotFound,
                    "The selected tour category is unavailable.");

            long? versionRootTourId = null;
            int versionNo = 1;
            if (request.SourceTourId.HasValue)
            {
                var source = await dbContext.Tours.AsNoTracking().SingleOrDefaultAsync(tour =>
                    tour.Id == request.SourceTourId.Value && tour.OperatorUserId == operatorId,
                    transactionCancellationToken);
                if (source is null)
                    return Result.Failure<CreateTourPackageResponse>(OperatorTourErrorCodes.TourNotFound,
                        "The source tour is unavailable.");
                if (source.Status is not (TourStatus.Approved or TourStatus.Published))
                    return Result.Failure<CreateTourPackageResponse>(OperatorTourErrorCodes.InvalidState,
                        "Only an approved or published tour can create a new draft version.");

                versionRootTourId = source.VersionRootTourId ?? source.Id;
                versionNo = await dbContext.Tours.AsNoTracking()
                    .Where(tour => (tour.VersionRootTourId ?? tour.Id) == versionRootTourId.Value)
                    .MaxAsync(tour => (int?)tour.VersionNo, transactionCancellationToken) + 1 ?? 2;
            }

            long[] destinationIds = request.DestinationIds.ToArray();
            var destinationNames = await dbContext.Destinations.AsNoTracking()
                .Where(destination => destinationIds.Contains(destination.Id))
                .ToDictionaryAsync(destination => destination.Id, destination => destination.Name,
                    transactionCancellationToken);
            if (destinationNames.Count != destinationIds.Length)
                return Result.Failure<CreateTourPackageResponse>(OperatorTourErrorCodes.DestinationNotFound,
                    "One or more selected destinations are unavailable.");

            var policy = await dbContext.CancellationPolicyTemplates.Include(template => template.Rules)
                .SingleOrDefaultAsync(template =>
                    template.Id == request.CancellationPolicyTemplateId && template.IsActive,
                    transactionCancellationToken);
            if (policy is null || policy.Rules.Count == 0)
                return Result.Failure<CreateTourPackageResponse>(OperatorTourErrorCodes.CancellationPolicyUnavailable,
                    "The selected cancellation policy is unavailable.");

            var poiIds = request.ItineraryItems.Select(item => item.PoiId).Distinct().ToArray();
            var availablePoiCount = await dbContext.PointsOfInterest.AsNoTracking()
                .CountAsync(poi => poiIds.Contains(poi.Id) && poi.Status == PointOfInterestStatus.Active,
                    transactionCancellationToken);
            if (availablePoiCount != poiIds.Length)
                return Result.Failure<CreateTourPackageResponse>(OperatorTourErrorCodes.PoiNotFound,
                    "One or more selected points of interest are unavailable.");

            var now = clock.UtcNow;
            var tour = Tour.CreateDraft(operatorId, request.CategoryId, request.Title, request.Description,
                request.BasePrice, request.DurationDays, destinationIds,
                OperatorTourValidation.BuildDestinationSummary(destinationIds.Select(id => destinationNames[id])), policy,
                request.ItineraryItems.Select(item => TourItineraryItem.Create(item.DayNo, item.SequenceNo,
                    item.PoiId, item.PlannedTime, item.StayDurationMinutes, item.Notes)),
                request.Schedules.Select(schedule => TourSchedule.Create(schedule.StartAtUtc, schedule.EndAtUtc,
                    schedule.MeetingPoint, schedule.TotalCapacity, now, schedule.MinimumCapacity,
                    schedule.RegistrationDeadlineAtUtc, schedule.AdultPrice ?? request.BasePrice,
                    schedule.ChildPrice, schedule.InfantPrice)), now,
                OperatorTourValidation.NormalizeText(request.Inclusions),
                OperatorTourValidation.NormalizeText(request.Exclusions),
                OperatorTourValidation.NormalizeText(request.Highlights),
                OperatorTourValidation.NormalizeText(request.TravellerRequirements),
                OperatorTourValidation.NormalizeText(request.TimeZoneId));
            dbContext.Tours.Add(tour);
            dbContext.TourPackageCreateOperations.Add(TourPackageCreateOperation.Create(
                operatorId, request.IdempotencyKey, fingerprint, tour, now));
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            tour.SetVersionLineage(versionRootTourId ?? tour.Id, versionNo);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return Result.Success(new CreateTourPackageResponse(tour.Id, nameof(TourStatus.Draft), versionNo));
        }, cancellationToken);
    }

    private static string ComputeFingerprint(CreateTourPackageCommand request)
    {
        // Normalize before hashing so trivially different text is one payload. Ordered lists
        // (destinations, itinerary) are hashed in the order sent and never sorted: their order is data.
        byte[] canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Title = request.Title?.Trim(),
            Description = request.Description?.Trim(),
            request.CategoryId,
            request.BasePrice,
            request.DurationDays,
            request.DestinationIds,
            request.CancellationPolicyTemplateId,
            Inclusions = OperatorTourValidation.NormalizeText(request.Inclusions),
            Exclusions = OperatorTourValidation.NormalizeText(request.Exclusions),
            Highlights = OperatorTourValidation.NormalizeText(request.Highlights),
            TravellerRequirements = OperatorTourValidation.NormalizeText(request.TravellerRequirements),
            TimeZoneId = OperatorTourValidation.NormalizeText(request.TimeZoneId),
            request.SourceTourId,
            request.ItineraryItems,
            request.Schedules,
        });
        return Convert.ToHexString(SHA256.HashData(canonicalPayload));
    }

    private static Result<CreateTourPackageResponse> AccessDenied() =>
        Result.Failure<CreateTourPackageResponse>(OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}