using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Itineraries;

[Collection(nameof(TripMateApiFactory))]
public sealed class ItineraryMutationEndpointTests
{
    [Fact]
    public async Task Accept_AcquiresTheSharedItineraryMutationLock()
    {
        var mutationLock = new RecordingItineraryMutationLock();
        await using var factory = new TripMateApiFactory(
            configureTestServices: services =>
            {
                services.RemoveAll<IItineraryMutationLock>();
                services.AddSingleton<IItineraryMutationLock>(mutationLock);
            });
        var itineraryId = await SeedManualItineraryAsync(factory, Itinerary.DraftStatus);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await client.PostAsync(
            $"/api/v1/itineraries/{itineraryId}/accept",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        mutationLock.AcquiredResources.Should().Equal(itineraryId);
    }

    [Fact]
    public async Task Accept_OwnerDraftTransitionsToActive()
    {
        await using var factory = new TripMateApiFactory();
        var itineraryId = await SeedManualItineraryAsync(factory, Itinerary.DraftStatus);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await client.PostAsync(
            $"/api/v1/itineraries/{itineraryId}/accept",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WithDbContextAsync(context =>
            context.Itineraries.SingleAsync(item => item.Id == itineraryId)))
            .Status.Should().Be(Itinerary.ActiveStatus);
    }

    [Fact]
    public async Task Accept_GroupMemberIsReadOnly()
    {
        await using var factory = new TripMateApiFactory();
        var itineraryId = await SeedGroupMembershipAsync(factory);
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Traveler);

        var response = await client.PostAsync(
            $"/api/v1/itineraries/{itineraryId}/accept",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RegenerateWithoutOrWithMalformedKeyReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        var itineraryId = await SeedManualItineraryAsync(factory, Itinerary.ActiveStatus);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var missing = await client.PostAsync(
            $"/api/v1/itineraries/{itineraryId}/regenerate",
            content: null);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "not-a-guid");
        var malformed = await client.PostAsync(
            $"/api/v1/itineraries/{itineraryId}/regenerate",
            content: null);

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AdjustItemsWithoutVisitIdsReturnsBadRequestInsteadOfServerError()
    {
        await using var factory = new TripMateApiFactory();
        var itineraryId = await SeedManualItineraryAsync(factory, Itinerary.ActiveStatus);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var missing = await client.PutAsJsonAsync(
            $"/api/v1/itineraries/{itineraryId}/items",
            new { });

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var nullValue = await client.PutAsJsonAsync(
            $"/api/v1/itineraries/{itineraryId}/items",
            new { orderedVisitPoiIds = (long[]?)null });

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        nullValue.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<long> SeedManualItineraryAsync(
        TripMateApiFactory factory,
        string status)
    {
        return await factory.WithDbContextAsync(async context =>
        {
            await SeedUsersAsync(context);
            var now = DateTimeOffset.UtcNow;
            var itinerary = Itinerary.CreateManual(1, "Mutation Trip", status, now);
            context.Itineraries.Add(itinerary);
            await context.SaveChangesAsync();
            return itinerary.Id;
        });
    }

    private static async Task<long> SeedGroupMembershipAsync(TripMateApiFactory factory)
    {
        return await factory.WithDbContextAsync(async context =>
        {
            await SeedUsersAsync(context);
            var now = DateTimeOffset.UtcNow;
            var itinerary = Itinerary.CreateManual(1, "Shared Trip", Itinerary.DraftStatus, now);
            context.Itineraries.Add(itinerary);
            await context.SaveChangesAsync();

            var group = TravelGroup.Create(itinerary.Id, 1, "Shared Group", now);
            group.AddMember(GroupMember.CreateHost(group, 1, now));
            group.AddMember(GroupMember.CreateMember(group, 2, now));
            context.TravelGroups.Add(group);
            await context.SaveChangesAsync();
            return itinerary.Id;
        });
    }

    private static async Task SeedUsersAsync(TestApiDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        context.Users.AddRange(
            new User
            {
                Id = 1,
                Email = $"owner-{Guid.NewGuid():N}@example.com",
                FullName = "Owner",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            new User
            {
                Id = 2,
                Email = $"member-{Guid.NewGuid():N}@example.com",
                FullName = "Member",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        await context.SaveChangesAsync();
    }

    private sealed class RecordingItineraryMutationLock : IItineraryMutationLock
    {
        public List<long> AcquiredResources { get; } = [];

        public Task AcquireAsync(long itineraryId, CancellationToken cancellationToken)
        {
            AcquiredResources.Add(itineraryId);
            return Task.CompletedTask;
        }
    }
}