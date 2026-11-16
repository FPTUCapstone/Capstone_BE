using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Complaints.PartialRefundProposals;

public sealed class PartialRefundProposalDeadlineOptions
{
    public const string SectionName = "PartialRefundProposalDeadline";

    /// <summary>SRS §3.1.4 job #16 is hourly.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    public int BatchSize { get; set; } = 100;
}

public sealed class PartialRefundProposalDeadlineOptionsValidator : IValidateOptions<PartialRefundProposalDeadlineOptions>
{
    public ValidateOptionsResult Validate(string? name, PartialRefundProposalDeadlineOptions options) =>
        options.SweepInterval <= TimeSpan.Zero || options.BatchSize <= 0
            ? ValidateOptionsResult.Fail("PartialRefundProposalDeadline options must be positive.")
            : ValidateOptionsResult.Success;
}

/// <summary>
/// The 3-day branch of the Complaint Deadline Job (SRS §3.1.4 #16, BR-153): an unanswered partial
/// refund proposal is declined and both parties are notified with MSG168.
/// </summary>
public sealed class PartialRefundProposalDeadlineProcessor(
    IApplicationDbContext dbContext,
    IPartialRefundProposalStore store,
    IDateTimeProvider clock,
    PartialRefundProposalDeadlineOptions options)
{
    public async Task<int> CloseOverdueAsync(CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;
        var overdue = await store.ListOverdueAsync(nowUtc, options.BatchSize, cancellationToken);
        int closed = 0;
        foreach (var proposal in overdue)
        {
            // One transaction per proposal: a Traveler answer that commits first wins (TryClose returns false).
            bool closedThis = await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
            {
                if (!await store.TryCloseAsync(proposal.ComplaintId, PostTripComplaintStatus.Declined, nowUtc,
                        transactionCancellationToken))
                {
                    return false;
                }

                dbContext.Notifications.AddRange(PartialRefundProposalNotifications.Expired(proposal, nowUtc));
                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return true;
            }, cancellationToken);
            if (closedThis) closed++;
        }

        return closed;
    }
}