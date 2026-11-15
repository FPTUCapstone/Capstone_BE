using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.LeaveTravelGroup;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class LeaveTravelGroupEndpointTests
{
    [Fact]
    public async Task Leave_WhenConcurrencyRetriesExhausted_ReturnsServiceUnavailable()
    {
        await using var factory = new TripMateApiFactory(configureTestServices: services =>
            services.Replace(ServiceDescriptor.Scoped<IGroupLeaveLock, AlwaysConflictingLeaveLock>()));
        using var traveler = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        traveler.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await traveler.PostAsync("/api/v1/travel-groups/1/leave", null);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Leave_RequiresTravelerAuthenticationAndIdempotencyKey()
    {
        await using var factory = new TripMateApiFactory();
        using var anonymous = factory.CreateClient();
        (await anonymous.PostAsync("/api/v1/travel-groups/1/leave", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var operatorClient = factory.CreateAuthenticatedClient(1, UserRole.TourOperator);
        operatorClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await operatorClient.PostAsync("/api/v1/travel-groups/1/leave", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var traveler = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        (await traveler.PostAsync("/api/v1/travel-groups/1/leave", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        traveler.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await traveler.PostAsync("/api/v1/travel-groups/0/leave", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Leave_LastHost_ReturnsOriginalResponseOnRetry()
    {
        await using var factory = new TripMateApiFactory();
        var groupId = await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var host = new User
            {
                Email = $"leave-host-{Guid.NewGuid():N}@example.com",
                FullName = "Leave Host",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.Users.Add(host);
            await db.SaveChangesAsync();
            var group = TravelGroup.Create(1, host.Id, "Leave Endpoint Group", now);
            db.TravelGroups.Add(group);
            db.GroupMembers.Add(GroupMember.CreateHost(group, host.Id, now));
            await db.SaveChangesAsync();
            return (GroupId: group.Id, HostUserId: host.Id);
        });

        using var client = factory.CreateAuthenticatedClient(groupId.HostUserId, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var first = await client.PostAsync($"/api/v1/travel-groups/{groupId.GroupId}/leave", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<LeaveTravelGroupResponse>();
        firstBody.Should().NotBeNull();
        firstBody!.GroupClosed.Should().BeTrue();
        firstBody.MembershipStatus.Should().Be(nameof(GroupMemberStatus.Left));

        var retry = await client.PostAsync($"/api/v1/travel-groups/{groupId.GroupId}/leave", null);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await retry.Content.ReadFromJsonAsync<LeaveTravelGroupResponse>()).Should().Be(firstBody);
    }

    private sealed class AlwaysConflictingLeaveLock : IGroupLeaveLock
    {
        public Task AcquireOperationLockAsync(
            long travelerUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new DbUpdateConcurrencyException("Simulated leave lock conflict.");

        public Task AcquireGroupLockAsync(long groupId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}