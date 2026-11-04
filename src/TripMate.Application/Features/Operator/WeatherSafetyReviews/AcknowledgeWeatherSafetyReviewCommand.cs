using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.WeatherSafetyReviews;

public sealed record AcknowledgeWeatherSafetyReviewCommand(
    long ReviewId,
    string? ImmediateInstructions = null)
    : IRequest<Result<WeatherSafetyReviewDto>>;

public sealed class AcknowledgeWeatherSafetyReviewCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IWeatherSafetyReviewNotifier reviewNotifier)
    : IRequestHandler<AcknowledgeWeatherSafetyReviewCommand, Result<WeatherSafetyReviewDto>>
{
    public async Task<Result<WeatherSafetyReviewDto>> Handle(
        AcknowledgeWeatherSafetyReviewCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AcknowledgeAsync(request, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Escalation won the race: acknowledge the state it left.
            dbContext.ClearTrackedEntities();
            return await AcknowledgeAsync(request, cancellationToken);
        }
    }

    private async Task<Result<WeatherSafetyReviewDto>> AcknowledgeAsync(
        AcknowledgeWeatherSafetyReviewCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<WeatherSafetyReviewDto>(WeatherSafetyReviewErrorCodes.Forbidden, "Tour Operator access is required.");
        }

        if (!await EmergencyOperatorAccess.IsEligibleOperatorAsync(dbContext, operatorId, cancellationToken))
        {
            return Result.Failure<WeatherSafetyReviewDto>(WeatherSafetyReviewErrorCodes.Forbidden, "Tour Operator account is not eligible.");
        }

        var review = await dbContext.WeatherSafetyReviews
            .Include(r => r.TourSchedule)
                .ThenInclude(s => s.Tour)
            .SingleOrDefaultAsync(r => r.Id == request.ReviewId, cancellationToken);

        if (review is null || review.TourSchedule.Tour.OperatorUserId != operatorId)
        {
            return Result.Failure<WeatherSafetyReviewDto>(WeatherSafetyReviewErrorCodes.NotFound, "Weather safety review could not be found.");
        }

        DateTimeOffset now = clock.UtcNow;

        if (review.Status == WeatherSafetyReview.StatusClosed)
        {
            return Result.Failure<WeatherSafetyReviewDto>(WeatherSafetyReviewErrorCodes.AlreadyClosed, "Closed weather review cannot be acknowledged.");
        }

        // Idempotency: if already acknowledged by this operator, return current state safely
        if (review.Status == WeatherSafetyReview.StatusAcknowledged)
        {
            return Result.Success(WeatherSafetyReviewDtoMapper.ToDto(
                review,
                await FindWeatherEventAsync(review, cancellationToken)));
        }

        string? beforeData = JsonSerializer.Serialize(new
        {
            review.Id,
            review.Status,
            review.UrgentNoticeSentAtUtc,
            review.AcknowledgementDeadlineUtc
        });

        bool acknowledged = review.Acknowledge(operatorId, now, request.ImmediateInstructions);
        if (!acknowledged)
        {
            return Result.Failure<WeatherSafetyReviewDto>("WEATHER_REVIEW_ACKNOWLEDGE_FAILED", "Failed to acknowledge review.");
        }

        string? afterData = JsonSerializer.Serialize(new
        {
            review.Id,
            review.Status,
            review.AcknowledgedAtUtc,
            review.AcknowledgedByOperatorId,
            review.ImmediateInstructions
        });

        var auditLog = AuditLog.CreateRecordedOutcome(
            actorUserId: operatorId,
            actionType: AuditActionTypes.WeatherSafetyReviewAcknowledged,
            affectedEntity: AuditEntityTypes.WeatherSafetyReview,
            affectedEntityId: review.Id,
            createdAtUtc: now,
            result: AuditOutcome.Success,
            reason: request.ImmediateInstructions ?? "Acknowledged safety hold",
            beforeData: beforeData,
            afterData: afterData);
        dbContext.AuditLogs.Add(auditLog);

        await dbContext.SaveChangesAsync(cancellationToken);

        await reviewNotifier.NotifyChangedAsync(
            operatorId,
            new WeatherSafetyReviewChanged(
                review.Id,
                review.TourScheduleId,
                WeatherSafetyReviewChangeTypes.Updated,
                now),
            cancellationToken);

        return Result.Success(WeatherSafetyReviewDtoMapper.ToDto(
            review,
            await FindWeatherEventAsync(review, cancellationToken)));
    }

    private Task<WeatherEvent?> FindWeatherEventAsync(
        WeatherSafetyReview review,
        CancellationToken cancellationToken) => dbContext.WeatherEvents
        .AsNoTracking()
        .Where(e => e.Provider == review.Provider
            && e.ProviderEventId == review.ProviderEventId)
        .OrderByDescending(e => e.UpdatedAtUtc)
        .FirstOrDefaultAsync(cancellationToken);
}
