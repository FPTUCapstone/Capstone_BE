using Microsoft.EntityFrameworkCore;

using TripMate.Domain.Entities;

namespace TripMate.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<PoiCategory> PoiCategories { get; }

    DbSet<PointOfInterest> PointsOfInterest { get; }

    DbSet<PoiOpeningHour> PoiOpeningHours { get; }

    DbSet<Tag> Tags { get; }

    DbSet<PoiTag> PoiTags { get; }

    DbSet<OperatorProfile> OperatorProfiles { get; }

    DbSet<OperatorDocument> OperatorDocuments { get; }

    DbSet<AuditLog> AuditLogs { get; }

    DbSet<Notification> Notifications { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}