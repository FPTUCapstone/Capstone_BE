using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.ManageInvitation;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupInvitationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRequestsWithSameKey_CreateOneInvitationAndReplaySameResult()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> GetOrCreateAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new GetOrCreateGroupInvitationCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(context),
                new RandomGroupInvitationCodeGenerator());
            var result = await handler.Handle(
                new GetOrCreateGroupInvitationCommand(seed.GroupId, seed.HostUserId, key),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value.InviteCode;
        }

        var first = GetOrCreateAsync();
        var second = GetOrCreateAsync();
        startGate.SetResult();
        var inviteCodes = await Task.WhenAll(first, second);

        inviteCodes[0].Should().Be(inviteCodes[1]);
        await using var verification = database.CreateDbContext();
        (await verification.GroupInvitations.CountAsync()).Should().Be(1);
        (await verification.GroupInvitationOperations.CountAsync()).Should().Be(1);
        (await verification.GroupMembers.CountAsync()).Should().Be(0);
    }

    private static async Task<(long HostUserId, long GroupId)> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        var host = new User
        {
            Email = "sql-invitation-host@example.com",
            FullName = "SQL Invitation Host",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.Add(host);
        await context.SaveChangesAsync();

        var itinerary = Itinerary.CreateManual(host.Id, "SQL Invitation Trip", Itinerary.ActiveStatus, now);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();

        var group = TravelGroup.Create(itinerary.Id, host.Id, "SQL Invitation Group", now);
        context.TravelGroups.Add(group);
        await context.SaveChangesAsync();
        return (host.Id, group.Id);
    }

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    }
}