using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.CreateTravelGroup;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateTravelGroupSqlServerTests
{
    private const string RejectHostConstraintName =
        "CK_TravelGroups_RejectHostForRollbackTest";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRequestsWithSameKey_ReturnOneGroupAndReplaySameResult()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var dateTimeProvider = new FixedDateTimeProvider();

        async Task<CreateTravelGroupResponse> CreateAsync()
        {
            await using var context = database.CreateDbContext();
            var handler = new CreateTravelGroupCommandHandler(context, dateTimeProvider);
            var result = await handler.Handle(
                new CreateTravelGroupCommand(seed.ItineraryId, "Concurrent Group", seed.UserId, key),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value;
        }

        var results = await Task.WhenAll(CreateAsync(), CreateAsync());

        results[0].GroupId.Should().Be(results[1].GroupId);
        await using var verificationContext = database.CreateDbContext();
        (await verificationContext.TravelGroups.CountAsync()).Should().Be(1);
        (await verificationContext.GroupMembers.CountAsync()).Should().Be(1);
        (await verificationContext.TravelGroupCreationRequests.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task HostInsertFailure_RollsBackGroupAndOperation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await database.ExecuteNonQueryAsync($"""
            ALTER TABLE social.GroupMembers
            ADD CONSTRAINT [{RejectHostConstraintName}]
            CHECK (user_id <> {seed.UserId} OR status <> 'Active');
            """);

        await using (var context = database.CreateDbContext())
        {
            var handler = new CreateTravelGroupCommandHandler(context, new FixedDateTimeProvider());
            var action = () => handler.Handle(
                new CreateTravelGroupCommand(
                    seed.ItineraryId,
                    "Rollback Group",
                    seed.UserId,
                    Guid.NewGuid()),
                CancellationToken.None);

            await action.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verificationContext = database.CreateDbContext();
        (await verificationContext.TravelGroups.CountAsync()).Should().Be(0);
        (await verificationContext.GroupMembers.CountAsync()).Should().Be(0);
        (await verificationContext.TravelGroupCreationRequests.CountAsync()).Should().Be(0);
    }

    private static async Task<(long UserId, long ItineraryId)> SeedAsync(
        SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        var user = new User
        {
            Email = "concurrent-group@example.com",
            FullName = "Concurrent Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var itinerary = new Itinerary
        {
            TravelerUserId = user.Id,
            Title = "Concurrent Trip",
            Status = "Active",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();
        return (user.Id, itinerary.Id);
    }

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 9, 13, 2, 0, 0, TimeSpan.Zero);
    }
}
