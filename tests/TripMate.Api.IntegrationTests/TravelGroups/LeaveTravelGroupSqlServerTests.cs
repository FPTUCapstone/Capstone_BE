using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.LeaveTravelGroup;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class LeaveTravelGroupSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentSameKey_LeavesOnceAndReplaysExactlyAfterRejoin()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<LeaveTravelGroupResponse> LeaveAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            var result = await CreateHandler(context).Handle(
                new LeaveTravelGroupCommand(seed.GroupId, seed.MemberId, key), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value;
        }

        var first = LeaveAsync();
        var second = LeaveAsync();
        gate.SetResult();
        var responses = await Task.WhenAll(first, second);
        responses[0].Should().Be(responses[1]);

        await using (var context = database.CreateDbContext())
        {
            var member = await context.GroupMembers.SingleAsync(item =>
                item.GroupId == seed.GroupId && item.UserId == seed.MemberId);
            member.Reactivate(new FixedClock().UtcNow.AddMinutes(1));
            await context.SaveChangesAsync();
        }

        await using (var context = database.CreateDbContext())
        {
            var replay = await CreateHandler(context).Handle(
                new LeaveTravelGroupCommand(seed.GroupId, seed.MemberId, key), CancellationToken.None);
            replay.IsSuccess.Should().BeTrue();
            replay.Value.Should().Be(responses[0]);
        }

        await using var verification = database.CreateDbContext();
        (await verification.GroupLeaveOperations.CountAsync()).Should().Be(1);
        (await verification.GroupMembers.SingleAsync(item => item.GroupId == seed.GroupId && item.UserId == seed.MemberId))
            .Status.Should().Be(GroupMemberStatus.Active);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentDifferentKeys_OneSucceedsAndOneConflicts()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Application.Common.Models.Result<LeaveTravelGroupResponse>> LeaveAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await CreateHandler(context).Handle(
                new LeaveTravelGroupCommand(seed.GroupId, seed.MemberId, Guid.NewGuid()), CancellationToken.None);
        }

        var first = LeaveAsync();
        var second = LeaveAsync();
        gate.SetResult();
        var results = await Task.WhenAll(first, second);
        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Single(result => result.IsFailure).ErrorCode.Should().Be(TravelGroupErrorCodes.AlreadyLeft);

        await using var verification = database.CreateDbContext();
        (await verification.GroupLeaveOperations.CountAsync()).Should().Be(1);
        (await verification.GroupMembers.SingleAsync(item => item.GroupId == seed.GroupId && item.UserId == seed.MemberId))
            .Status.Should().Be(GroupMemberStatus.Left);
    }

    private static LeaveTravelGroupCommandHandler CreateHandler(ApplicationDbContext context) =>
        new(context, new FixedClock(), new SqlServerGroupLeaveLock(context),
            new ItineraryAccessService(context),
            NullLogger<LeaveTravelGroupCommandHandler>.Instance);

    private static async Task<(long GroupId, long MemberId)> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedClock().UtcNow;
        var host = new User
        {
            Email = $"sql-leave-host-{Guid.NewGuid():N}@example.com",
            FullName = "Leave Host",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var member = new User
        {
            Email = $"sql-leave-member-{Guid.NewGuid():N}@example.com",
            FullName = "Leave Member",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.AddRange(host, member);
        await context.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(host.Id, "Leave SQL Trip", Itinerary.ActiveStatus, now);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();
        var group = TravelGroup.Create(itinerary.Id, host.Id, "Leave SQL Group", now);
        context.TravelGroups.Add(group);
        context.GroupMembers.Add(GroupMember.CreateHost(group, host.Id, now));
        context.GroupMembers.Add(GroupMember.CreateMember(group, member.Id, now));
        await context.SaveChangesAsync();
        return (group.Id, member.Id);
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
    }
}