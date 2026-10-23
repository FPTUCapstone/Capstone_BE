using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class TripReviewMediaSqlServerTests
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
    internal static TripReviewMediaOperation Operation() => TripReviewMediaOperation.Reserve(Guid.NewGuid(), Guid.NewGuid(), 1, 1, 0, "review-" + Guid.NewGuid().ToString("N"), "image/png", ".png", 1, 1, 1, Now);
    internal static TripReview Parent(long booking = 1, long owner = 1) => TripReview.CreatePublished(booking, owner, null, 1, 5, "Title", "Content", null, null, false, "Name", null, Now);
    internal static async Task<SqlServerTestDatabase> Database()
    {
        var db = await SqlServerTestDatabase.CreateAsync();
        try { await db.ExecuteNonQueryAsync(TripReviewMediaMigrationTests.Seed); return db; }
        catch { await db.DisposeAsync(); throw; }
    }

    [SqlServerFact]
    public async Task EfRoundTrip_IdentityNavigationUtcAndVersion()
    {
        await using var db = await Database();
        var operation = Operation(); var parent = Parent();
        await using (var writer = db.CreateDbContext())
        {
            writer.Set<TripReviewMediaOperation>().Add(operation);
            await writer.SaveChangesAsync();
            operation.Version.Should().HaveCount(8);
            var old = operation.Version.ToArray();
            operation.TryRecordUpload("https://images.example.invalid/a", 6000000, Now.AddMinutes(1)).Should().BeTrue();
            operation.TryAdopt(Now.AddMinutes(2)).Should().BeTrue();
            writer.Set<TripReviewMedia>().Add(TripReviewMedia.Link(parent, operation, Now.AddMinutes(2)));
            await writer.SaveChangesAsync();
            parent.Id.Should().BeGreaterThan(0); operation.Version.Should().NotEqual(old);
        }
        await using var reader = db.CreateDbContext();
        var link = await reader.Set<TripReviewMedia>().Include(x => x.Operation).Include(x => x.Review).SingleAsync();
        link.Review.Id.Should().Be(parent.Id); link.Operation.State.Should().Be("Adopted");
        link.Operation.CreatedAtUtc.Should().Be(Now); link.Operation.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        link.Operation.StoredByteLength.Should().Be(6000000);
        await using var deletion = db.CreateDbContext();
        deletion.Remove(await deletion.TripReviews.SingleAsync(x => x.Id == parent.Id));
        Func<Task> remove = () => deletion.SaveChangesAsync(); await remove.Should().ThrowAsync<DbUpdateException>();
    }

    [SqlServerFact]
    public async Task CleanedHydratesAsTerminal_AndStaleVersionCannotWrite()
    {
        await using var db = await Database();
        var op = Operation();
        await using (var seed = db.CreateDbContext()) { seed.Add(op); await seed.SaveChangesAsync(); }
        await using var stale = db.CreateDbContext(); var old = await stale.Set<TripReviewMediaOperation>().SingleAsync();
        await db.ExecuteNonQueryAsync("UPDATE social.TripReviewMediaOperations SET state='Cleaned',cleaned_at='2026-09-29';");
        await using (var read = db.CreateDbContext())
        {
            var terminal = await read.Set<TripReviewMediaOperation>().SingleAsync();
            terminal.TryAdopt(Now).Should().BeFalse(); terminal.TryMarkCleanupPending(Now).Should().BeFalse();
            terminal.TryRecordUpload("https://images.example.invalid/a", 1, Now).Should().BeFalse();
        }
        old.TryMarkCleanupPending(Now).Should().BeTrue();
        Func<Task> save = () => stale.SaveChangesAsync(); await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}