using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.Authorization;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Admin.Users.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin.Users;

[Collection(nameof(TripMateApiFactory))]
public sealed class UnlockUserAccountEndpointTests
{
    [Fact]
    public async Task Unlock_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await SendUnlockAsync(client, 20, "unlock-http-001");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await factory.WithDbContextAsync(async db =>
        {
            AuditLog audit = await db.AuditLogs.SingleAsync();
            audit.ActionType.Should().Be(AuditActionTypes.UserUnlock);
            audit.AffectedEntity.Should().Be(AuditEntityTypes.User);
            audit.AffectedEntityId.Should().Be(20);
            audit.ActorUserId.Should().BeNull();
            audit.Result.Should().Be(AuditOutcome.Failure);
            using var metadata = JsonDocument.Parse(audit.AfterData!);
            metadata.RootElement.GetProperty("auditMetadata").GetProperty("errorCode").GetString()
                .Should().Be(AuthorizationAuditErrorCodes.Unauthenticated);
            return true;
        });
    }

    [Fact]
    public async Task Unlock_WhenCallerIsNotAdministrator_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Traveler);

        var response = await SendUnlockAsync(client, 20, "unlock-http-002");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await factory.WithDbContextAsync(async db =>
        {
            AuditLog audit = await db.AuditLogs.SingleAsync();
            audit.ActorUserId.Should().Be(10);
            audit.ActionType.Should().Be(AuditActionTypes.UserUnlock);
            audit.AffectedEntity.Should().Be(AuditEntityTypes.User);
            audit.AffectedEntityId.Should().Be(20);
            audit.Result.Should().Be(AuditOutcome.Failure);
            using var metadata = JsonDocument.Parse(audit.AfterData!);
            metadata.RootElement.GetProperty("auditMetadata").GetProperty("errorCode").GetString()
                .Should().Be(AuthorizationAuditErrorCodes.Forbidden);
            return true;
        });
    }

    [Fact]
    public async Task Unlock_WithoutIdempotencyKey_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Administrator);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users/20/unlock",
            new { reason = "Verified account owner" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("user.unlock_request_invalid");
    }

    [Fact]
    public async Task Unlock_WithBlankReason_ReturnsBadRequestWithUnlockValidationCode()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Administrator);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users/20/unlock")
        {
            Content = JsonContent.Create(new { reason = "   " })
        };
        request.Headers.Add("Idempotency-Key", "unlock-http-invalid-reason");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("user.unlock_request_invalid");
    }

    [Fact]
    public async Task Unlock_WhenTargetIsValid_Returns200AndPersistsOneOperation()
    {
        await using var factory = new TripMateApiFactory();
        await SeedLockedTravelerAsync(factory, 20);
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Administrator);

        var response = await SendUnlockAsync(client, 20, "unlock-http-004");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("userId").GetInt64().Should().Be(20);
        payload.RootElement.GetProperty("restoredStatus").GetString().Should().Be("Active");

        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == 20)).Status.Should().Be(AccountStatus.Active);
            (await db.UserUnlockOperations.CountAsync()).Should().Be(1);
            (await db.AuditLogs.CountAsync()).Should().Be(1);
            Notification notification = await db.Notifications.SingleAsync();
            notification.UserId.Should().Be(20);
            notification.Type.Should().Be(UserAdministrationNotificationTypes.AccountUnlocked);
            notification.Status.Should().Be(NotificationStatus.Pending);
            return true;
        });
    }

    [Fact]
    public async Task Unlock_WhenSameKeyAndPayloadAreRetried_ReplaysResponseWithoutSecondMutation()
    {
        await using var factory = new TripMateApiFactory();
        await SeedLockedTravelerAsync(factory, 20);
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Administrator);

        var first = await SendUnlockAsync(client, 20, "unlock-http-replay");
        var replay = await SendUnlockAsync(client, 20, "unlock-http-replay");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        await factory.WithDbContextAsync(async db =>
        {
            (await db.UserUnlockOperations.CountAsync()).Should().Be(1);
            (await db.AuditLogs.CountAsync()).Should().Be(1);
            return true;
        });
    }

    [Fact]
    public async Task Unlock_WhenTargetDoesNotExist_ReturnsNotFoundWithErrorCode()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Administrator);

        var response = await SendUnlockAsync(client, 404, "unlock-http-005");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("user.not_found");
    }

    private static Task<HttpResponseMessage> SendUnlockAsync(HttpClient client, long userId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/users/{userId}/unlock")
        {
            Content = JsonContent.Create(new { reason = "Verified account owner" })
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task SeedLockedTravelerAsync(TripMateApiFactory factory, long userId)
    {
        await factory.WithDbContextAsync(async db =>
        {
            var administrator = new User
            {
                Id = 10,
                FullName = "Administrator",
                Role = UserRole.Administrator,
                Status = AccountStatus.Active,
            };
            var traveler = new User
            {
                Id = userId,
                FullName = "Locked traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
            };
            traveler.RecordLock(AccountStatus.Active, administrator.Id, DateTimeOffset.UtcNow, "Security review");
            db.Users.AddRange(administrator, traveler);
            await db.SaveChangesAsync();
            return true;
        });
    }
}