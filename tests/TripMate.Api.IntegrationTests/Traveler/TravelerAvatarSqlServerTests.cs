using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.Traveler.Profile.Common;
using TripMate.Application.Features.Traveler.Profile.Update;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Traveler;

public sealed class TravelerAvatarSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ReplaceAvatar_CommitsReferenceAndCleanup_RetriesFailedDelete()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long userId = await SeedTravelerAsync(database);
        string oldId = $"tripmate/avatars/{userId}/old";
        string newId = $"tripmate/avatars/{userId}/new";
        await using (var context = database.CreateDbContext())
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
            user.AvatarPublicId = oldId;
            user.AvatarUrl = "https://cdn.example.test/old.png";
            await context.SaveChangesAsync();
        }

        var config = Configuration(database.ConnectionString);
        var storage = new AvatarStorageStub(newId) { DeleteSucceeds = false };
        var journal = new SqlTravelerAvatarCleanupJournal(config);
        await using (var context = database.CreateDbContext())
        {
            var handler = new UpdateTravelerProfileCommandHandler(context,
                new Caller(userId), new Clock(), storage, journal);
            var result = await handler.Handle(new UpdateTravelerProfileCommand(
                "Updated Traveler", null, null, [137, 80, 78, 71, 13, 10, 26, 10], "image/png", false),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        await using (var context = database.CreateDbContext())
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
            user.AvatarPublicId.Should().Be(newId);
            user.AvatarUrl.Should().Be($"https://cdn.example.test/{newId}.png");
        }
        (await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM dbo.TravelerAvatarCleanupOutbox WHERE public_id='{oldId}'"))
            .Should().Be(1);
        (await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM dbo.TravelerAvatarCleanupOutbox WHERE public_id='{newId}'"))
            .Should().Be(0);

        var worker = new TravelerAvatarCleanupBackgroundService(config, storage, new Clock(),
            NullLogger<TravelerAvatarCleanupBackgroundService>.Instance);
        await worker.ProcessOneAsync(CancellationToken.None);
        storage.Deleted.Should().ContainSingle().Which.Should().Be(oldId);
        (await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM dbo.TravelerAvatarCleanupOutbox WHERE public_id='{oldId}'"))
            .Should().Be(1);

        storage.DeleteSucceeds = true;
        await database.ExecuteNonQueryAsync($"UPDATE dbo.TravelerAvatarCleanupOutbox SET not_before_at='2026-10-12' WHERE public_id='{oldId}'");
        await worker.ProcessOneAsync(CancellationToken.None);
        storage.Deleted.Should().HaveCount(2);
        (await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM dbo.TravelerAvatarCleanupOutbox WHERE public_id='{oldId}'"))
            .Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CleanupWorker_DoesNotDeleteCurrentlyReferencedAvatar()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long userId = await SeedTravelerAsync(database);
        string currentId = $"tripmate/avatars/{userId}/current";
        await using (var context = database.CreateDbContext())
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
            user.AvatarPublicId = currentId;
            await context.SaveChangesAsync();
        }
        var config = Configuration(database.ConnectionString);
        await new SqlTravelerAvatarCleanupJournal(config).ReserveAsync(currentId, Now, CancellationToken.None);
        var storage = new AvatarStorageStub(currentId);

        var worker = new TravelerAvatarCleanupBackgroundService(config, storage, new Clock(),
            NullLogger<TravelerAvatarCleanupBackgroundService>.Instance);
        await worker.ProcessOneAsync(CancellationToken.None);

        storage.Deleted.Should().BeEmpty();
        (await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM dbo.TravelerAvatarCleanupOutbox WHERE public_id='{currentId}'"))
            .Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RemoveAvatar_CommitsNullReferenceBeforeOldAssetCleanup()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long userId = await SeedTravelerAsync(database);
        string oldId = $"tripmate/avatars/{userId}/old";
        await using (var context = database.CreateDbContext())
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
            user.AvatarPublicId = oldId;
            user.AvatarUrl = "https://cdn.example.test/old.png";
            await context.SaveChangesAsync();
        }
        var config = Configuration(database.ConnectionString);
        var storage = new AvatarStorageStub(oldId);
        await using (var context = database.CreateDbContext())
        {
            var result = await new UpdateTravelerProfileCommandHandler(context,
                new Caller(userId), new Clock(), storage, new SqlTravelerAvatarCleanupJournal(config))
                .Handle(new UpdateTravelerProfileCommand("Original Traveler", null, null,
                    null, null, true), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        await using (var context = database.CreateDbContext())
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
            user.AvatarPublicId.Should().BeNull();
            user.AvatarUrl.Should().BeNull();
        }
        storage.Deleted.Should().BeEmpty();
        await new TravelerAvatarCleanupBackgroundService(config, storage, new Clock(),
            NullLogger<TravelerAvatarCleanupBackgroundService>.Instance)
            .ProcessOneAsync(CancellationToken.None);
        storage.Deleted.Should().ContainSingle().Which.Should().Be(oldId);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SaveFailsAfterUpload_KeepsDurableReservationAndRetriesDeletion()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long userId = await SeedTravelerAsync(database);
        string newId = $"tripmate/avatars/{userId}/orphan";
        var config = Configuration(database.ConnectionString);
        var storage = new AvatarStorageStub(newId);
        await using (var context = database.CreateDbContext(new FailAfterSaveInterceptor()))
        {
            var result = await new UpdateTravelerProfileCommandHandler(context,
                new Caller(userId), new Clock(), storage, new SqlTravelerAvatarCleanupJournal(config))
                .Handle(new UpdateTravelerProfileCommand("Changed", null, null,
                    [137, 80, 78, 71, 13, 10, 26, 10], "image/png", false), CancellationToken.None);
            result.IsFailure.Should().BeTrue();
        }

        await using (var context = database.CreateDbContext())
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
            user.FullName.Should().Be("Original Traveler");
            user.AvatarPublicId.Should().BeNull();
        }
        (await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM dbo.TravelerAvatarCleanupOutbox WHERE public_id='{newId}'"))
            .Should().Be(1);
        await new TravelerAvatarCleanupBackgroundService(config, storage,
            new Clock(DateTimeOffset.UtcNow.AddMinutes(1)),
            NullLogger<TravelerAvatarCleanupBackgroundService>.Instance)
            .ProcessOneAsync(CancellationToken.None);
        storage.Deleted.Should().ContainSingle().Which.Should().Be(newId);
        (await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM dbo.TravelerAvatarCleanupOutbox WHERE public_id='{newId}'"))
            .Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PhoneTakenAfterInitialCheck_ReturnsCatalogConflictFromUniqueIndex()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long userId = await SeedTravelerAsync(database);
        long otherId = await SeedTravelerAsync(database);
        const string phone = "0912345678";
        await using var context = database.CreateDbContext(
            new ClaimPhoneBeforeSaveInterceptor(database.ConnectionString, otherId, phone));
        var result = await new UpdateTravelerProfileCommandHandler(context,
            new Caller(userId), new Clock(), new AvatarStorageStub("unused"),
            new SqlTravelerAvatarCleanupJournal(Configuration(database.ConnectionString)))
            .Handle(new UpdateTravelerProfileCommand("Changed", phone, null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelerProfileErrorCodes.PhoneAlreadyExists);
        await using var verification = database.CreateDbContext();
        (await verification.Users.SingleAsync(candidate => candidate.Id == userId))
            .PhoneNumber.Should().BeNull();
        (await verification.Users.SingleAsync(candidate => candidate.Id == otherId))
            .PhoneNumber.Should().Be(phone);
    }

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
        }).Build();

    private static async Task<long> SeedTravelerAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var user = new User
        {
            FullName = "Original Traveler",
            Email = $"traveler-{Guid.NewGuid():N}@example.test",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private sealed class Caller(long userId) : ICurrentUserService
    {
        public long? UserId => userId;
        public string? Role => nameof(UserRole.Traveler);
    }

    private sealed class Clock(DateTimeOffset? now = null) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now ?? Now;
    }

    private sealed class FailAfterSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected failure after SQL writes, before commit.");
    }

    private sealed class ClaimPhoneBeforeSaveInterceptor(string connectionString, long otherId,
        string phone) : SaveChangesInterceptor
    {
        private int triggered;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref triggered, 1) != 0) return result;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                "UPDATE dbo.Users SET phone_number=@phone WHERE user_id=@id", connection);
            command.Parameters.AddWithValue("@phone", phone);
            command.Parameters.AddWithValue("@id", otherId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return result;
        }
    }

    private sealed class AvatarStorageStub(string publicId) : ITravelerAvatarStorage
    {
        public bool DeleteSucceeds { get; set; } = true;
        public List<string> Deleted { get; } = [];
        public string AllocatePublicId(long userId) => publicId;
        public Task<TravelerAvatarUploadResult> UploadAsync(TravelerAvatarUpload upload, CancellationToken cancellationToken) =>
            Task.FromResult(TravelerAvatarUploadResult.Succeeded(
                new Uri($"https://cdn.example.test/{upload.PublicId}.png")));
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
        {
            Deleted.Add(id);
            return Task.FromResult(DeleteSucceeds);
        }
    }
}