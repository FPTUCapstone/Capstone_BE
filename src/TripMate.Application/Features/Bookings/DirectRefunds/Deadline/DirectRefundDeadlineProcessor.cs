using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Deadline;

public sealed class DirectRefundDeadlineOptions
{
    public const string SectionName = "DirectRefundDeadline";

    /// <summary>Hourly sweep to find overdue transfer windows.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    public int BatchSize { get; set; } = 50;
}

public sealed class DirectRefundDeadlineOptionsValidator : IValidateOptions<DirectRefundDeadlineOptions>
{
    public ValidateOptionsResult Validate(string? name, DirectRefundDeadlineOptions options) =>
        options.SweepInterval <= TimeSpan.Zero || options.BatchSize <= 0
            ? ValidateOptionsResult.Fail("DirectRefundDeadline options must be positive.")
            : ValidateOptionsResult.Success;
}

/// <summary>
/// Auto-completes cases whose 72-hour Traveler confirmation window has expired with no response.
/// Per spec §8: status moves from AwaitingTravelerConfirmation → Completed with method AutoCompletedAfter72Hours.
/// Also flags AwaitingTransfer cases whose transfer deadline has passed as overdue.
/// </summary>
public sealed class DirectRefundDeadlineProcessor(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    DirectRefundDeadlineOptions options)
{
    public async Task<int> ProcessOverdueAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = clock.UtcNow;
        int processed = 0;

        // Auto-complete: AwaitingTravelerConfirmation cases whose 72h window has passed
        var overdueConfirmations = await dbContext.DirectRefundCases
            .Where(c => c.Status == DirectRefundCase.StatusAwaitingTravelerConfirmation)
            .Join(dbContext.DirectRefundTransfers,
                c => c.Id, t => t.CaseId,
                (c, t) => new { Case = c, Transfer = t })
            .Where(x => x.Transfer.ConfirmationDeadlineAtUtc <= nowUtc)
            .Select(x => x.Case.Id)
            .Distinct()
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (long caseId in overdueConfirmations)
        {
            bool completed = await dbContext.ExecuteInTransactionAsync(async ct =>
            {
                var directCase = await dbContext.DirectRefundCases
                    .Where(c => c.Id == caseId
                                && c.Status == DirectRefundCase.StatusAwaitingTravelerConfirmation)
                    .SingleOrDefaultAsync(ct);

                if (directCase is null) return false;

                directCase.Complete(DirectRefundCase.MethodAutoCompletedAfter72Hours, "1.0", nowUtc);
                await dbContext.SaveChangesAsync(ct);
                return true;
            }, cancellationToken);

            if (completed) processed++;
        }

        // Flag overdue: AwaitingTransfer cases whose TransferDeadline has passed
        var overdueTransfers = await dbContext.DirectRefundCases
            .Where(c => c.Status == DirectRefundCase.StatusAwaitingTransfer
                        && c.TransferDeadlineAtUtc.HasValue
                        && c.TransferDeadlineAtUtc <= nowUtc
                        && !c.IsOverdueFlag)
            .Select(c => c.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (long caseId in overdueTransfers)
        {
            await dbContext.ExecuteInTransactionAsync(async ct =>
            {
                var directCase = await dbContext.DirectRefundCases
                    .Where(c => c.Id == caseId
                                && c.Status == DirectRefundCase.StatusAwaitingTransfer
                                && !c.IsOverdueFlag)
                    .SingleOrDefaultAsync(ct);

                if (directCase is null) return false;

                directCase.FlagOverdue(nowUtc);
                await dbContext.SaveChangesAsync(ct);
                return true;
            }, cancellationToken);
        }

        return processed;
    }
}