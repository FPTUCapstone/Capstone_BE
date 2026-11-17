using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.PostTripComplaints.Deadlines;

public sealed class OperatorComplaintDeadlineOptions
{
    public const string SectionName = "OperatorComplaintDeadline";
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
    public int BatchSize { get; set; } = 100;
}

public sealed class OperatorComplaintDeadlineOptionsValidator : IValidateOptions<OperatorComplaintDeadlineOptions>
{
    public ValidateOptionsResult Validate(string? name, OperatorComplaintDeadlineOptions options) =>
        options.SweepInterval <= TimeSpan.Zero || options.BatchSize <= 0
            ? ValidateOptionsResult.Fail("OperatorComplaintDeadline options must be positive.")
            : ValidateOptionsResult.Success;
}

/// <summary>
/// Closes an unanswered initial complaint response and an unanswered final
/// reconsideration. The latter is the bounded-negotiation branch of UC-75.
/// </summary>
public sealed class OperatorComplaintDeadlineProcessor(
    IApplicationDbContext dbContext, IDateTimeProvider clock, OperatorComplaintDeadlineOptions options)
{
    public async Task<int> ExpireOverdueAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        List<long> ids = await dbContext.PostTripComplaints.AsNoTracking()
            .Where(item =>
                (item.Status == PostTripComplaintStatus.AwaitingOperatorResponse &&
                 item.ResponseDeadlineUtc <= now) ||
                (item.Status == PostTripComplaintStatus.AwaitingOperatorReconsideration &&
                 item.OperatorReconsiderationDeadlineUtc != null &&
                 item.OperatorReconsiderationDeadlineUtc <= now))
            .OrderBy(item => item.Status == PostTripComplaintStatus.AwaitingOperatorReconsideration
                ? item.OperatorReconsiderationDeadlineUtc
                : item.ResponseDeadlineUtc)
            .ThenBy(item => item.Id)
            .Select(item => item.Id).Take(options.BatchSize).ToListAsync(cancellationToken);
        int expired = 0;
        foreach (long complaintId in ids)
        {
            bool changed = await dbContext.ExecuteInSerializableTransactionAsync(async token =>
            {
                PostTripComplaint? complaint = await dbContext.PostTripComplaints.SingleOrDefaultAsync(item => item.Id == complaintId,
                    token);
                if (complaint is null) return false;
                bool reconsiderationExpired = complaint.DeclineExpiredOperatorReconsideration(now);
                if (!reconsiderationExpired && !complaint.ExpireOperatorResponse(now)) return false;
                dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(null, "PostTripComplaintExpired",
                    Notification.PostTripComplaintRelatedEntityType, complaint.Id, now, AuditOutcome.Success));
                dbContext.Notifications.Add(new Notification
                {
                    UserId = complaint.TravelerUserId,
                    Channel = NotificationChannel.Push,
                    Type = "PostTripComplaintExpired",
                    Title = reconsiderationExpired
                        ? "Final refund proposal deadline passed"
                        : "Complaint response deadline passed",
                    Body = reconsiderationExpired
                        ? "The Tour Operator did not revise the partial-refund proposal before the final deadline."
                        : "The Tour Operator did not respond before the complaint deadline.",
                    RelatedEntityType = Notification.PostTripComplaintRelatedEntityType,
                    RelatedEntityId = complaint.Id,
                    Status = NotificationStatus.Pending,
                    CreatedAtUtc = now,
                });
                await dbContext.SaveChangesAsync(token);
                return true;
            }, cancellationToken);
            if (changed) expired++;
        }
        return expired;
    }
}
