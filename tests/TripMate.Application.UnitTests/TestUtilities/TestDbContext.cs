using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Configurations;

namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>
/// Minimal InMemory-backed stand-in for the real EF Core DbContext (which lives in
/// TripMate.Infrastructure) so Application-layer handlers can be tested without depending on
/// the Infrastructure project.
/// </summary>
public class TestDbContext(DbContextOptions<TestDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<OperatorProfile> OperatorProfiles => Set<OperatorProfile>();

    public DbSet<OperatorDocument> OperatorDocuments => Set<OperatorDocument>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<Message> Messages => Set<Message>();

    public bool ThrowOnSaveConcurrency { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnSaveConcurrency)
        {
            throw new DbUpdateConcurrencyException("Concurrency conflict simulated in test.");
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OperatorProfileConfiguration).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public static TestDbContext Create()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContext(options);
    }
}
