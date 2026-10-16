using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.SemanticPoc;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services.SemanticPoc;

/// <summary>Read-only batched SQL authority; no index-provided metadata controls publication.</summary>
public sealed class SqlSemanticTourAuthority(IApplicationDbContext context, IDateTimeProvider clock) : ISemanticTourAuthority
{
    public async Task<IReadOnlyList<SemanticTourDocument>> ResolveAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        if (ids.Count > SemanticModel.MaxCorpus)
        {
            throw new ArgumentOutOfRangeException(nameof(ids));
        }
        if (ids.Count == 0)
        {
            return [];
        }
        var now = clock.UtcNow.ToUniversalTime();
        var rows = await context.Tours.AsNoTracking().Where(t => ids.Contains(t.Id)
            && (t.Status == TourStatus.Published || (t.Status == TourStatus.Approved && t.PublishedAtUtc.HasValue && t.PublishedAtUtc.Value <= now))
            && t.OperatorProfile.ApprovalStatus == OperatorApprovalStatus.Approved
            && t.OperatorProfile.User.Status == AccountStatus.Active)
            .Select(t => new { t.Id, t.Title, Operator = t.OperatorProfile.CompanyName, Destinations = t.Destinations.Select(d => d.Destination.Name).ToArray() })
            .ToArrayAsync(cancellationToken);
        return rows.Select(t => new SemanticTourDocument(t.Id, t.Title, t.Destinations, t.Operator, true)).ToArray();
    }
}
