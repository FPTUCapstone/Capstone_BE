using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TravelGroups.GetMembers;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GetTravelGroupMembersSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ActiveMember_ReadsOnlyActiveMembers_AndJoinedAtIsUtc()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var now = new DateTimeOffset(2026, 9, 21, 16, 0, 0, TimeSpan.FromHours(7));

        await using (var setup = database.CreateDbContext())
        {
            var host = CreateTraveler("SQL Host", now);
            var member = CreateTraveler("SQL Active Member", now);
            var removed = CreateTraveler("SQL Removed Member", now);
            setup.Users.AddRange(host, member, removed);
            await setup.SaveChangesAsync();

            var itinerary = Itinerary.CreateManual(host.Id, "UC19 SQL itinerary", Itinerary.ActiveStatus, now);
            setup.Itineraries.Add(itinerary);
            await setup.SaveChangesAsync();

            var group = TravelGroup.Create(itinerary.Id, host.Id, "UC19 SQL Group", now);
            setup.TravelGroups.Add(group);
            await setup.SaveChangesAsync();

            setup.GroupMembers.Add(GroupMember.CreateHost(group, host.Id, now));
            setup.GroupMembers.Add(GroupMember.CreateMember(group, member.Id, now));
            var removedMembership = GroupMember.CreateMember(group, removed.Id, now);
            typeof(GroupMember).GetProperty(nameof(GroupMember.Status))!
                .SetValue(removedMembership, GroupMemberStatus.Removed);
            setup.GroupMembers.Add(removedMembership);
            await setup.SaveChangesAsync();

            var queryCounter = new TestCommandCounterInterceptor();
            await using var queryContext = database.CreateDbContext(queryCounter);
            var handler = new GetTravelGroupMembersQueryHandler(
                queryContext,
                NullLogger<GetTravelGroupMembersQueryHandler>.Instance);
            var result = await handler.Handle(
                new GetTravelGroupMembersQuery(group.Id, member.Id),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.Members.Should().HaveCount(2);
            result.Value.Members.Should().ContainSingle(item => item.IsHost);
            result.Value.Members.Should().NotContain(item => item.DisplayName == "SQL Removed Member");
            result.Value.Members.Should().OnlyContain(item => item.JoinedAtUtc.Offset == TimeSpan.Zero);
            // Authorization and member projection must read one SQL snapshot.
            queryCounter.CommandCount.Should().Be(1);
        }
    }

    private static User CreateTraveler(string fullName, DateTimeOffset now) => new()
    {
        Email = $"{Guid.NewGuid():N}@example.com",
        FullName = fullName,
        Role = UserRole.Traveler,
        Status = AccountStatus.Active,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };
}