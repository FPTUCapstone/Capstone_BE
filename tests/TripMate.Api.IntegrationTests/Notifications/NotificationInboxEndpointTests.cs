using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Notifications.GetInbox;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Notifications;

[Collection(nameof(TripMateApiFactory))]
public sealed class NotificationInboxEndpointTests
{
    [Fact]
    public async Task Inbox_ReturnsOnlyOwnInAppMessages_WithExclusiveCursor()
    {
        await using var factory = new TripMateApiFactory();
        var ids = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(101, UserRole.Traveler);

        var first = await client.GetFromJsonAsync<GetNotificationInboxResponse>(
            "/api/v1/notifications?limit=1");
        first!.Items.Should().ContainSingle().Which.Id.Should().Be(ids.Newer);
        first.NextBeforeId.Should().Be(ids.Newer);

        var second = await client.GetFromJsonAsync<GetNotificationInboxResponse>(
            $"/api/v1/notifications?limit=1&beforeId={first.NextBeforeId}");
        second!.Items.Should().ContainSingle().Which.Id.Should().Be(ids.Older);
        second.NextBeforeId.Should().BeNull();
    }

    [Fact]
    public async Task Inbox_UnreadOnlyAndInvalidPagination_AreEnforced()
    {
        await using var factory = new TripMateApiFactory();
        var ids = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(101, UserRole.Traveler);

        (await client.PostAsync($"/api/v1/notifications/{ids.Newer}/read", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var inbox = await client.GetFromJsonAsync<GetNotificationInboxResponse>(
            "/api/v1/notifications?unreadOnly=true");
        inbox!.Items.Should().ContainSingle().Which.Id.Should().Be(ids.Older);
        (await client.GetAsync("/api/v1/notifications?limit=51"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/v1/notifications?beforeId=0"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MarkRead_OtherRecipientIsHidden_AndOwnRetryIsSafe()
    {
        await using var factory = new TripMateApiFactory();
        var ids = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(101, UserRole.Traveler);

        (await client.PostAsync($"/api/v1/notifications/{ids.OtherUser}/read", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/v1/notifications/{ids.Push}/read", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/v1/notifications/{ids.Older}/read", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsync($"/api/v1/notifications/{ids.Older}/read", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var inbox = await client.GetFromJsonAsync<GetNotificationInboxResponse>(
            "/api/v1/notifications");
        inbox!.Items.Single(item => item.Id == ids.Older).IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task InboxAndRead_RequireAuthentication()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        (await client.GetAsync("/api/v1/notifications"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/api/v1/notifications/1/read", null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static Task<(long Older, long Newer, long OtherUser, long Push)> SeedAsync(
        TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async db =>
        {
            var now = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
            var older = NewNotification(101, NotificationChannel.InApp, now);
            var otherUser = NewNotification(202, NotificationChannel.InApp, now);
            var push = NewNotification(101, NotificationChannel.Push, now);
            var newer = NewNotification(101, NotificationChannel.InApp, now.AddMinutes(1));
            db.Notifications.AddRange(older, otherUser, push, newer);
            await db.SaveChangesAsync();
            return (older.Id, newer.Id, otherUser.Id, push.Id);
        });

    private static Notification NewNotification(long userId, NotificationChannel channel, DateTimeOffset createdAt) =>
        new()
        {
            UserId = userId,
            Channel = channel,
            Type = Notification.TravelGroupHostTransferredType,
            Title = "Group Host changed",
            Body = "You are now the Host.",
            RelatedEntityType = Notification.TravelGroupRelatedEntityType,
            RelatedEntityId = 17,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = createdAt,
        };
}