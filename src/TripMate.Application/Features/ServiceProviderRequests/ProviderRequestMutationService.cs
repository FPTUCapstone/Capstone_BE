using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.CommercialServices.Booking;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.ServiceProviderRequests;

public sealed class ProviderRequestMutationService(IApplicationDbContext db,
    IServiceProviderRequestLock mutationLock, IDateTimeProvider clock)
{
    public async Task<Result<ProviderRequestDetail>> ExecuteAsync(long actorId, long bookingId,
        Guid key, string operationType, string targetStatus, string? reason, decimal? actualAmount,
        string rowVersion, CancellationToken token)
    {
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            bookingId,
            operationType,
            targetStatus,
            reason,
            actualAmount = actualAmount?.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            rowVersion = Convert.ToBase64String(Convert.FromBase64String(rowVersion))
        })));
        try
        {
            return await db.ExecuteInTransactionAsync(async transactionToken =>
            {
                // Execution-strategy retries must re-read state after a rollback or uncertain commit.
                db.ClearTrackedEntities();
                await mutationLock.AcquireOperationAsync(actorId, key, transactionToken);
                await mutationLock.AcquireBookingAsync(bookingId, transactionToken);

                if (!await ProviderRequestAccess.IsActiveUserAsync(db, actorId, transactionToken))
                    return Failure(ProviderRequestErrors.Forbidden, "An active Service Provider account is required.");

                var booking = await ProviderRequestAccess.Owned(db, actorId).Include(candidate => candidate.Service)
                    .SingleOrDefaultAsync(candidate => candidate.Id == bookingId, transactionToken);
                if (booking is null) return Failure(ProviderRequestErrors.NotFound, "The service booking request could not be found.");

                var previous = await db.ServiceProviderRequestOperations.AsNoTracking()
                    .SingleOrDefaultAsync(operation => operation.ActorUserId == actorId && operation.IdempotencyKey == key, transactionToken);
                if (previous is not null)
                {
                    if (previous.ServiceBookingId != bookingId || previous.OperationType != operationType || previous.RequestHash != hash)
                        return Failure(ProviderRequestErrors.IdempotencyMismatch, "The idempotency key was used for a different request.");
                    return Result.Success(JsonSerializer.Deserialize<ProviderRequestDetail>(previous.ResponseJson)!);
                }

                if (!booking.RowVersion.AsSpan().SequenceEqual(Convert.FromBase64String(rowVersion)))
                    return Failure(ProviderRequestErrors.VersionConflict, "The request changed. Reload its details.");

                var now = clock.UtcNow;
                var before = booking.Status;
                if (booking.TryExpire(now) || booking.TryDefaultComplete(now))
                {
                    ProviderRequestEvents.Record(db, booking, null, before, now);
                    await db.SaveChangesAsync(transactionToken);
                    return Failure(ProviderRequestErrors.InvalidState, "The request's response window has ended.");
                }
                if (booking.SubmitKey.HasValue && targetStatus == ServiceBooking.Confirmed)
                {
                    await mutationLock.AcquireAvailabilityAsync(booking.ServiceId, booking.StartAtUtc, transactionToken);
                    var slot = await db.ServiceAvailability.AsNoTracking().SingleOrDefaultAsync(
                        item => item.ServiceId == booking.ServiceId && item.StartsAtUtc == booking.StartAtUtc, transactionToken);
                    var confirmed = await CommercialServiceBookingHandler.CountConfirmedAsync(
                        db, booking.ServiceId, booking.StartAtUtc, transactionToken);
                    if (slot is null || booking.StartAtUtc <= now || booking.Quantity > slot.Quantity - confirmed)
                        return Failure(ProviderRequestErrors.InvalidState, "MSG71: This time has insufficient availability.");
                }

                var changed = operationType == ServiceProviderRequestOperation.Decision
                    ? booking.TryDecide(targetStatus, reason, actorId, now)
                    : booking.TryRecordOutcome(targetStatus, actualAmount, actorId, now);
                if (!changed) return Failure(ProviderRequestErrors.InvalidState, "The request is no longer eligible for this action.");

                ProviderRequestEvents.Record(db, booking, actorId, before, now);
                await db.SaveChangesAsync(transactionToken);
                var traveler = await db.Users.AsNoTracking().SingleAsync(user => user.Id == booking.TravelerUserId, transactionToken);
                var response = ProviderRequestDetail.From(booking, traveler);
                db.ServiceProviderRequestOperations.Add(ServiceProviderRequestOperation.Create(booking, actorId,
                    key, operationType, hash, JsonSerializer.Serialize(response), now));
                await db.SaveChangesAsync(transactionToken);
                return Result.Success(response);
            }, token);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ClearTrackedEntities();
            return Failure(ProviderRequestErrors.VersionConflict, "The request changed. Reload its details.");
        }
        catch
        {
            db.ClearTrackedEntities();
            throw;
        }
    }

    private static Result<ProviderRequestDetail> Failure(string code, string message) =>
        Result.Failure<ProviderRequestDetail>(code, message);
}

public static class ProviderRequestEvents
{
    public const string EntityType = "ServiceBooking";
    public const string ActionType = "ServiceBookingRequestTransition";
    public const string NotificationType = "ServiceBookingRequestUpdated";

    public static void Record(IApplicationDbContext db, ServiceBooking booking, long? actorId,
        string previousStatus, DateTimeOffset now)
    {
        db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(actorId, ActionType, EntityType, booking.Id,
            now, AuditOutcome.Success, reason: booking.RejectionReason,
            beforeData: JsonSerializer.Serialize(new { Status = previousStatus }),
            afterData: JsonSerializer.Serialize(new
            {
                booking.Status,
                booking.ActualAmount,
                booking.CommissionAmount,
                booking.IsAutoCompleted
            })));
        var body = booking.Status switch
        {
            ServiceBooking.Confirmed => "MSG72: Your service booking request has been confirmed.",
            ServiceBooking.Rejected => booking.RejectionReason!,
            ServiceBooking.Expired => "Your service booking request expired because the provider did not respond within 24 hours.",
            ServiceBooking.Cancelled => "MSG186: You cancelled your service booking request.",
            ServiceBooking.NoShow => "Your service booking outcome was recorded as no-show.",
            _ => "Your service booking outcome was recorded as completed."
        };
        db.Notifications.Add(new Notification
        {
            UserId = booking.TravelerUserId,
            Channel = NotificationChannel.InApp,
            Type = NotificationType,
            Title = booking.Status == ServiceBooking.Rejected ? "MSG73: Service booking request rejected" : "Service booking update",
            Body = body,
            RelatedEntityType = EntityType,
            RelatedEntityId = booking.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = now
        });
    }
}
