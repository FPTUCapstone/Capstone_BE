using System.Data.Common;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Reviews.Media;

using static TripMate.Api.IntegrationTests.Reviews.TripReviewMediaSqlServerTests;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class TripReviewMediaJournalTests
{
    [Fact]
    public void JournalIsRegistered()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        TripMate.Infrastructure.DependencyInjection.AddInfrastructure(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(provider);
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IReviewMediaJournal>(scope.ServiceProvider).Should().BeOfType<SqlServerReviewMediaJournal>();
    }

    [Infrastructure.SqlServerFact]
    public async Task ServiceReservationUploadAndAdoption_UsesServiceNamespace()
    {
        await using var db = await Database();
        await SeedServiceAsync(db);
        await using var writer = db.CreateDbContext();
        var batch = ServiceBatch(1);
        var reviewableRecord = ReviewableRecordRef.ServiceBooking(1);
        var journal = new SqlServerReviewMediaJournal(writer);

        var reserved = await journal.ReserveAsync(reviewableRecord, 1, batch, default);
        reserved.IsSuccess.Should().BeTrue();
        var claim = await journal.ClaimUploadAsync(batch[0].Id, 1, Now, default);
        claim.IsSuccess.Should().BeTrue();
        var uploaded = await journal.CompleteUploadAsync(claim.Value, 1,
            new(ReviewMediaStorageOutcome.Success, "https://images.example.invalid/service", 1),
            Now, default);
        uploaded.IsSuccess.Should().BeTrue();

        await using var tx = await writer.Database.BeginTransactionAsync();
        var parent = TripReview.CreatePublishedForService(1, 1, 1, 5,
            "Service stay", "The service was clean and helpful.", null, null, false,
            "Media One", ReviewContentPolicy.ActiveVersion, Now);
        (await journal.AdoptAsync(batch[0].BatchId, 1, [uploaded.Value], parent, Now, default))
            .IsSuccess.Should().BeTrue();
        await tx.CommitAsync();

        await using var read = db.CreateDbContext();
        var operation = await read.TripReviewMediaOperations.SingleAsync();
        operation.BookingId.Should().BeNull();
        operation.ServiceBookingId.Should().Be(1);
        operation.State.Should().Be(TripReviewMediaOperation.Adopted);
        var review = await read.TripReviews.SingleAsync(x => x.ServiceBookingId == 1);
        review.BookingId.Should().BeNull();
        review.ServiceBookingId.Should().Be(1);
        review.PoiId.Should().Be(1);
        (await read.TripReviewMedia.SingleAsync()).TripReviewId.Should().Be(review.Id);
    }

    [Infrastructure.SqlServerFact]
    public async Task EqualCommerceId_CannotCleanServiceMediaBatch()
    {
        await using var db = await Database();
        await SeedServiceAsync(db);
        await using var writer = db.CreateDbContext();
        var batch = ServiceBatch(1);
        var service = ReviewableRecordRef.ServiceBooking(1);
        var journal = new SqlServerReviewMediaJournal(writer);
        var reserved = await journal.ReserveAsync(service, 1, batch, default);
        reserved.IsSuccess.Should().BeTrue();

        var wrongNamespace = await journal.MarkCleanupPendingAsync(batch[0].BatchId,
            ReviewableRecordRef.CommerceBooking(1), 1, reserved.Value, Now, default);
        wrongNamespace.ErrorCode.Should().Be(ReviewMediaErrors.MissingOrForeign);

        (await journal.MarkCleanupPendingAsync(batch[0].BatchId, service, 1,
            reserved.Value, Now, default)).IsSuccess.Should().BeTrue();
        await using var read = db.CreateDbContext();
        var operation = await read.TripReviewMediaOperations.SingleAsync();
        operation.BookingId.Should().BeNull();
        operation.ServiceBookingId.Should().Be(1);
        operation.State.Should().Be(TripReviewMediaOperation.CleanupPending);
    }
    [Infrastructure.SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdoptionVersusCleanup_BothWinnerOrderings(bool adoptionWins)
    {
        await using var db = await Database(); var batch = Batch(); ReviewMediaVersion[] versions;
        await using (var seed = db.CreateDbContext()) versions = await Upload(seed, batch);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = Task.Run(async () =>
        {
            await using var context = db.CreateDbContext(new BatchRaceBarrier(locked, contending, true));
            var journal = new SqlServerReviewMediaJournal(context);
            if (!adoptionWins) return await journal.MarkCleanupPendingAsync(batch[0].BatchId, 1, 1, versions, Now, default);
            await using var tx = await context.Database.BeginTransactionAsync();
            var result = await journal.AdoptAsync(batch[0].BatchId, 1, versions, Parent(), Now, default);
            if (result.IsSuccess) await tx.CommitAsync();
            return result;
        });
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var loser = Task.Run(async () =>
        {
            await using var other = db.CreateDbContext(new BatchRaceBarrier(locked, contending, false)); var journal = new SqlServerReviewMediaJournal(other);
            if (adoptionWins) return await journal.MarkCleanupPendingAsync(batch[0].BatchId, 1, 1, versions, Now, default);
            await using var otherTx = await other.Database.BeginTransactionAsync();
            return await journal.AdoptAsync(batch[0].BatchId, 1, versions, Parent(), Now, default);
        });
        (await winner.WaitAsync(TimeSpan.FromSeconds(30))).IsSuccess.Should().BeTrue();
        (await loser.WaitAsync(TimeSpan.FromSeconds(30))).IsFailure.Should().BeTrue();
        await using var read = db.CreateDbContext();
        (await read.TripReviewMediaOperations.Select(x => x.State).ToListAsync()).Should().OnlyContain(x => x == (adoptionWins ? TripReviewMediaOperation.Adopted : TripReviewMediaOperation.CleanupPending));
        (await read.TripReviewMedia.CountAsync()).Should().Be(adoptionWins ? 2 : 0);
        (await read.TripReviews.CountAsync(x => x.BookingId == 1)).Should().Be(adoptionWins ? 1 : 0);
    }

    [Infrastructure.SqlServerFact]
    public async Task MediaInsertSqlFailure_RollsBackParentAndAdoption()
    {
        await using var db = await Database(); var fault = new FailAfterMediaWrite(); await using var writer = db.CreateDbContext(fault); var batch = Batch(); var versions = await Upload(writer, batch);
        await using var tx = await writer.Database.BeginTransactionAsync(); var parent = Parent(); writer.Add(parent); await writer.SaveChangesAsync();
        Func<Task> adopt = () => new SqlServerReviewMediaJournal(writer).AdoptAsync(batch[0].BatchId, 1, versions, parent, Now, default);
        var failure = await adopt.Should().ThrowAsync<SqlException>();
        failure.Which.Number.Should().Be(51000);
        fault.VerifiedPersistedWrites.Should().BeTrue();
        await using var read = db.CreateDbContext();
        (await read.TripReviews.CountAsync(x => x.BookingId == 1)).Should().Be(0);
        (await read.TripReviewMedia.CountAsync()).Should().Be(0);
        (await read.TripReviewMediaOperations.CountAsync(x => x.State == TripReviewMediaOperation.Uploaded)).Should().Be(2);
    }

    [Infrastructure.SqlServerTheory]
    [InlineData("stale")]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("mixed")]
    [InlineData("partly-adopted")]
    [InlineData("extra")]
    [InlineData("cleaned")]
    public async Task CleanupRejectsInvalidBatchWithoutPartialWrites(string kind)
    {
        await using var db = await Database(); await using var writer = db.CreateDbContext(); var batch = Batch(); var versions = await Upload(writer, batch);
        if (kind == "stale") versions[0] = versions[0] with { Version = [] };
        if (kind == "missing") versions = versions[..1];
        if (kind == "extra") versions = [.. versions, new(Guid.NewGuid(), new byte[8])];
        if (kind == "mixed") await db.ExecuteNonQueryAsync($"UPDATE social.TripReviewMediaOperations SET state='CleanupPending' WHERE operation_id='{batch[0].Id}';");
        if (kind == "partly-adopted") await db.ExecuteNonQueryAsync($"UPDATE social.TripReviewMediaOperations SET state='Adopted',adopted_at='2026-09-29' WHERE operation_id='{batch[0].Id}';");
        if (kind == "cleaned") await db.ExecuteNonQueryAsync($"UPDATE social.TripReviewMediaOperations SET state='Cleaned',cleaned_at='2026-09-29' WHERE operation_id='{batch[0].Id}';");
        var before = await db.ExecuteScalarAsync<string>("SELECT * FROM social.TripReviewMediaOperations ORDER BY operation_id FOR JSON PATH");
        (await new SqlServerReviewMediaJournal(writer).MarkCleanupPendingAsync(batch[0].BatchId, 1, kind == "foreign" ? 2 : 1, versions, Now, default)).IsFailure.Should().BeTrue();
        (await db.ExecuteScalarAsync<string>("SELECT * FROM social.TripReviewMediaOperations ORDER BY operation_id FOR JSON PATH")).Should().Be(before);
    }
    private static TripReviewMediaOperation[] Batch(int count = 2, long owner = 1)
    {
        var batch = Guid.NewGuid();
        return Enumerable.Range(0, count).Select(i => TripReviewMediaOperation.Reserve(Guid.NewGuid(), batch, 1, owner, (byte)i,
            "review-" + Guid.NewGuid().ToString("N"), "image/png", ".png", 1, 1, 1, Now)).ToArray();
    }

    private static TripReviewMediaOperation[] ServiceBatch(int count = 2, long owner = 1)
    {
        var batch = Guid.NewGuid();
        return Enumerable.Range(0, count).Select(i => TripReviewMediaOperation.ReserveForService(
            Guid.NewGuid(), batch, 1, owner, (byte)i,
            "review-" + Guid.NewGuid().ToString("N"), "image/png", ".png", 1, 1, 1, Now)).ToArray();
    }

    private static Task SeedServiceAsync(Infrastructure.SqlServerTestDatabase db) =>
        db.ExecuteNonQueryAsync("""
            INSERT catalog.POICategories(name) VALUES(N'Test category');
            INSERT catalog.POIs(category_id,name,latitude,longitude)
            VALUES(1,N'Canonical POI',16,108);
            INSERT commercial.ServiceProviders(name,service_category)
            VALUES(N'Provider','Hotel');
            INSERT commercial.Services(provider_id,service_category,name,poi_id,price_amount,price_unit)
            VALUES(1,'Hotel',N'Service One',1,0,'PerNight');
            INSERT commercial.ServiceBookings(traveler_user_id,service_id,start_datetime,total_price,status,provider_reference)
            VALUES(1,1,'2026-10-01T02:00:00',0,'Completed',N'SB-MEDIA');
            """);
    private static async Task<ReviewMediaVersion[]> Upload(ApplicationDbContext db, TripReviewMediaOperation[] batch)
    {
        var journal = new SqlServerReviewMediaJournal(db);
        var reserved = await journal.ReserveAsync(1, 1, batch, default); reserved.IsSuccess.Should().BeTrue();
        var versions = new List<ReviewMediaVersion>();
        foreach (var item in reserved.Value)
        {
            var claim = await journal.ClaimUploadAsync(item.OperationId, 1, Now, default);
            claim.IsSuccess.Should().BeTrue();
            var uploaded = await journal.CompleteUploadAsync(claim.Value, 1,
                new(ReviewMediaStorageOutcome.Success, "https://images.example.invalid/" + item.OperationId, 6000000), Now, default);
            uploaded.IsSuccess.Should().BeTrue(); versions.Add(uploaded.Value);
        }
        return versions.ToArray();
    }
    [Infrastructure.SqlServerTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task ReserveUploadAdoptReplay_IsAtomic(int count)
    {
        await using var db = await Database(); await using var writer = db.CreateDbContext();
        var batch = Batch(count); var versions = await Upload(writer, batch);
        var journal = new SqlServerReviewMediaJournal(writer);
        await using var tx = await writer.Database.BeginTransactionAsync(); var parent = Parent();
        (await journal.AdoptAsync(batch[0].BatchId, 1, versions, parent, Now, default)).IsSuccess.Should().BeTrue();
        (await journal.AdoptAsync(batch[0].BatchId, 1, versions, parent, Now, default)).IsSuccess.Should().BeTrue();
        await tx.CommitAsync();
        await using var read = db.CreateDbContext();
        (await read.TripReviewMedia.CountAsync()).Should().Be(count);
        (await read.TripReviewMediaOperations.CountAsync(x => x.State == TripReviewMediaOperation.Adopted)).Should().Be(count);
    }
    [Infrastructure.SqlServerTheory]
    [InlineData("foreign")]
    [InlineData("stale")]
    [InlineData("missing")]
    [InlineData("wrong-parent")]
    [InlineData("extra")]
    public async Task AdoptionBusinessFailure_RollsBackInsertedParent(string kind)
    {
        await using var db = await Database(); await using var writer = db.CreateDbContext();
        var batch = Batch(); var versions = await Upload(writer, batch); var journal = new SqlServerReviewMediaJournal(writer);
        if (kind == "stale") versions[0] = versions[0] with { Version = new byte[8] };
        if (kind == "missing") versions = versions[..1];
        if (kind == "extra") versions = [.. versions, new(Guid.NewGuid(), new byte[8])];
        await using var tx = await writer.Database.BeginTransactionAsync(); var parent = Parent(owner: kind == "wrong-parent" ? 2 : 1);
        writer.Add(parent); await writer.SaveChangesAsync();
        (await journal.AdoptAsync(batch[0].BatchId, kind == "foreign" ? 2 : 1, versions, parent, Now, default)).IsFailure.Should().BeTrue();
        Func<Task> commit = () => tx.CommitAsync(); await commit.Should().ThrowAsync<Exception>();
        await using var read = db.CreateDbContext();
        (await read.TripReviews.CountAsync(x => x.BookingId == 1)).Should().Be(0);
        (await read.TripReviewMedia.CountAsync()).Should().Be(0);
        (await read.TripReviewMediaOperations.CountAsync(x => x.State == TripReviewMediaOperation.Uploaded)).Should().Be(2);
    }
    [Infrastructure.SqlServerFact]
    public async Task CleanupMixedBatchReplay_AndUploadConflicts()
    {
        await using var db = await Database(); await using var writer = db.CreateDbContext(); var batch = Batch();
        var journal = new SqlServerReviewMediaJournal(writer); var reserve = await journal.ReserveAsync(1, 1, batch, default);
        reserve.IsSuccess.Should().BeTrue(); var versions = reserve.Value.ToArray();
        var claim = await journal.ClaimUploadAsync(versions[0].OperationId, 1, Now, default);
        var upload = await journal.CompleteUploadAsync(claim.Value, 1,
            new(ReviewMediaStorageOutcome.Success, "https://images.example.invalid/a", 1), Now, default);
        upload.IsSuccess.Should().BeTrue(); versions[0] = upload.Value;
        (await journal.CompleteUploadAsync(claim.Value, 1,
            new(ReviewMediaStorageOutcome.Success, "https://images.example.invalid/b", 1), Now, default)).IsFailure.Should().BeTrue();
        (await journal.MarkCleanupPendingAsync(batch[0].BatchId, 1, 1, versions, Now, default)).IsSuccess.Should().BeTrue();
        (await journal.MarkCleanupPendingAsync(batch[0].BatchId, 1, 1, versions, Now, default)).IsSuccess.Should().BeTrue();
        await using var read = db.CreateDbContext(); var rows = await read.TripReviewMediaOperations.ToListAsync();
        rows.Should().OnlyContain(x => x.State == TripReviewMediaOperation.CleanupPending);
        rows.Count(x => x.DeliveryUrl != null).Should().Be(1);
    }
    [Infrastructure.SqlServerFact]
    public async Task ForeignReserveAndNoTransactionAdopt_AreRejected()
    {
        await using var db = await Database(); await using var writer = db.CreateDbContext(); var journal = new SqlServerReviewMediaJournal(writer);
        var foreign = await journal.ReserveAsync(1, 2, Batch(owner: 2), default);
        foreign.IsFailure.Should().BeTrue();
        foreign.ErrorCode.Should().Be(ReviewMediaErrors.MissingOrForeign);
        (await journal.AdoptAsync(Guid.NewGuid(), 1, [], Parent(), Now, default)).IsFailure.Should().BeTrue();
        (await writer.TripReviewMediaOperations.CountAsync()).Should().Be(0);
    }

    [Infrastructure.SqlServerFact]
    public async Task ReservationUniqueConflict_RollsBackEntireNewBatch()
    {
        await using var db = await Database(); await using var writer = db.CreateDbContext();
        var journal = new SqlServerReviewMediaJournal(writer); var existing = Batch(1);
        (await journal.ReserveAsync(1, 1, existing, default)).IsSuccess.Should().BeTrue();
        var batch = Batch();
        batch[1] = TripReviewMediaOperation.Reserve(Guid.NewGuid(), batch[0].BatchId, 1, 1, 1, existing[0].PublicId, "image/png", ".png", 1, 1, 1, Now);
        Func<Task> reserve = () => journal.ReserveAsync(1, 1, batch, default);
        var failure = await reserve.Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<SqlException>().Which.Number.Should().BeOneOf(2601, 2627);
        await using var read = db.CreateDbContext();
        (await read.TripReviewMediaOperations.CountAsync()).Should().Be(1);
        (await read.TripReviewMediaOperations.SingleAsync()).Id.Should().Be(existing[0].Id);
    }

    [Infrastructure.SqlServerFact]
    public async Task UploadWithoutCurrentFence_RejectsWithoutChangingReservedOperation()
    {
        await using var db = await Database(); await using var writer = db.CreateDbContext();
        var journal = new SqlServerReviewMediaJournal(writer); var batch = Batch(1);
        var reserve = await journal.ReserveAsync(1, 1, batch, default); reserve.IsSuccess.Should().BeTrue();
        var fabricated = new ReviewMediaUploadClaim(batch[0].Id, Guid.NewGuid(), batch[0].PublicId);
        var rejected = await journal.CompleteUploadAsync(fabricated, 1,
            new(ReviewMediaStorageOutcome.Success, "https://images.example.invalid/a", 1), Now, default);
        rejected.ErrorCode.Should().Be(ReviewMediaErrors.StaleVersion);
        await using var read = db.CreateDbContext(); var operation = await read.TripReviewMediaOperations.SingleAsync();
        operation.State.Should().Be(TripReviewMediaOperation.Reserved);
        operation.Version.Should().Equal(reserve.Value[0].Version);
        operation.DeliveryUrl.Should().BeNull();
    }

    [Infrastructure.SqlServerFact]
    public async Task AdoptionReplay_WithDifferentParent_IsRejectedWithoutChangingOriginalLinks()
    {
        await using var db = await Database(); var batch = Batch(); ReviewMediaVersion[] versions; long originalId;
        await using (var writer = db.CreateDbContext())
        {
            versions = await Upload(writer, batch);
            await using var tx = await writer.Database.BeginTransactionAsync(); var parent = Parent();
            (await new SqlServerReviewMediaJournal(writer).AdoptAsync(batch[0].BatchId, 1, versions, parent, Now, default)).IsSuccess.Should().BeTrue();
            await tx.CommitAsync(); originalId = parent.Id;
        }
        await using (var other = db.CreateDbContext())
        {
            await using var tx = await other.Database.BeginTransactionAsync();
            // A new instance cannot claim the identity of the already-published parent.
            var rejected = await new SqlServerReviewMediaJournal(other).AdoptAsync(batch[0].BatchId, 1, versions, Parent(), Now, default);
            rejected.ErrorCode.Should().Be(ReviewMediaErrors.InvalidState);
        }
        await using var read = db.CreateDbContext();
        (await read.TripReviewMedia.Select(x => x.TripReviewId).ToListAsync()).Should().HaveCount(2).And.OnlyContain(x => x == originalId);
        (await read.TripReviews.CountAsync(x => x.BookingId == 1)).Should().Be(1);
    }

    [Infrastructure.SqlServerFact]
    public async Task PartiallyAdoptedBatch_IsNotSilentlyRepaired()
    {
        await using var db = await Database(); await using var writer = db.CreateDbContext(); var batch = Batch(); var versions = await Upload(writer, batch);
        await db.ExecuteNonQueryAsync($"UPDATE social.TripReviewMediaOperations SET state='Adopted',adopted_at='2026-09-29' WHERE operation_id='{batch[0].Id}';");
        var before = await db.ExecuteScalarAsync<string>("SELECT * FROM social.TripReviewMediaOperations ORDER BY operation_id FOR JSON PATH");
        await using var tx = await writer.Database.BeginTransactionAsync();
        var rejected = await new SqlServerReviewMediaJournal(writer).AdoptAsync(batch[0].BatchId, 1, versions, Parent(), Now, default);
        rejected.ErrorCode.Should().Be(ReviewMediaErrors.InvalidState);
        (await db.ExecuteScalarAsync<string>("SELECT * FROM social.TripReviewMediaOperations ORDER BY operation_id FOR JSON PATH")).Should().Be(before);
        await using var read = db.CreateDbContext();
        (await read.TripReviewMedia.CountAsync()).Should().Be(0);
        (await read.TripReviews.CountAsync(x => x.BookingId == 1)).Should().Be(0);
    }

    private sealed class BatchRaceBarrier(TaskCompletionSource locked, TaskCompletionSource contending, bool winner) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!winner && command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal)) contending.TrySetResult();
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (winner && command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal))
            {
                locked.TrySetResult();
                await contending.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailAfterMediaWrite : SaveChangesInterceptor
    {
        public bool VerifiedPersistedWrites { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var context = (ApplicationDbContext)eventData.Context!;
            if (!context.ChangeTracker.Entries<TripReviewMedia>().Any()) return result;
            // Inspect actual persisted rows inside the publication transaction before injecting failure.
            (await context.TripReviewMedia.CountAsync(cancellationToken)).Should().Be(2);
            (await context.TripReviewMediaOperations.CountAsync(x => x.State == TripReviewMediaOperation.Adopted, cancellationToken)).Should().Be(2);
            (await context.TripReviews.CountAsync(x => x.BookingId == 1, cancellationToken)).Should().Be(1);
            VerifiedPersistedWrites = true;
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "THROW 51000,'Injected after media write',1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return result;
        }
    }
}