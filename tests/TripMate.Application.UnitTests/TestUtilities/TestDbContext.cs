using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>
/// Minimal InMemory-backed stand-in for the real EF Core DbContext.
/// </summary>
public class TestDbContext(DbContextOptions<TestDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Message> Messages => Set<Message>();

    public static TestDbContext Create()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContext(options);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Message>().HasKey(m => m.MessageCode);
    }
}