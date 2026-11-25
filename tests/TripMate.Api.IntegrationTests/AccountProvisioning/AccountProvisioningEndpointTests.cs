using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.AccountProvisioning;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.AccountProvisioning;

[Collection(nameof(TripMateApiFactory))]
public sealed class AccountProvisioningEndpointTests
{
    [Fact]
    public async Task AdministratorCreatesStaffAndReceivesMetadataWithoutCredentials()
    {
        using var factory = Factory();
        await Seed(factory, 7103, UserRole.Administrator, false);
        using var client = factory.CreateAuthenticatedClient(7103, UserRole.Administrator);
        var response = await client.PostAsJsonAsync("/api/v1/admin/staff-accounts", new
        {
            fullName = "New Staff",
            email = "new-staff@example.test",
            phoneNumber = "0901234567",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        body.RootElement.TryGetProperty("temporaryPassword", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("passwordHash", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("password", out _).Should().BeFalse();
        var duplicate = await client.PostAsJsonAsync("/api/v1/admin/staff-accounts", new
        {
            fullName = "Duplicate",
            email = "NEW-STAFF@example.test",
            phoneNumber = "0901234568",
        });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RequiredPasswordChangeCanRestoreSessionButCannotReadBusinessData()
    {
        using var factory = Factory();
        await Seed(factory, 7108, UserRole.ServiceProvider, true);
        const string token = "provisioning-refresh-cookie";
        var jwt = factory.Services.GetRequiredService<IJwtTokenService>();
        await factory.WithDbContextAsync(async db =>
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = 7108,
                TokenHash = jwt.HashRefreshToken(token),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync();
            return true;
        });
        using var client = factory.CreateAuthenticatedClient(7108, UserRole.ServiceProvider);
        client.DefaultRequestHeaders.Add("Cookie", $"tripmate_refresh={token}");
        var refreshed = await client.PostAsync("/api/v1/auth/web/refresh", null);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("data").GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        (await client.GetAsync("/api/v1/provider/service-booking-requests")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/api/v1/auth/change-password")]
    [InlineData("/api/v1/auth/web/change-password")]
    public async Task RequiredPasswordChangeCanReachPasswordValidation(string path)
    {
        using var factory = Factory();
        await Seed(factory, 7104, UserRole.ServiceProvider, true);
        using var client = factory.CreateAuthenticatedClient(7104, UserRole.ServiceProvider);
        var response = await client.PostAsJsonAsync(path, new { currentPassword = "WrongPass1!", newPassword = "NewPass2!" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("MSG17").And.NotContain("auth.password_change_required");
    }

    [Theory]
    [InlineData("/api/v1/auth/logout")]
    [InlineData("/api/v1/auth/web/logout")]
    public async Task RequiredPasswordChangeAllowsLogout(string path)
    {
        using var factory = Factory();
        await Seed(factory, 7105, UserRole.ServiceProvider, true);
        using var client = factory.CreateAuthenticatedClient(7105, UserRole.ServiceProvider);
        var response = await client.PostAsJsonAsync(path, new { refreshToken = "unknown-session" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
    [Theory]
    [InlineData(UserRole.Staff, "staff-accounts")]
    [InlineData(UserRole.Administrator, "service-provider-accounts")]
    [InlineData(UserRole.Traveler, "staff-accounts")]
    [InlineData(UserRole.ServiceProvider, "service-provider-accounts")]
    public async Task ProvisioningRequiresExactActingRole(UserRole role, string route)
    {
        using var factory = Factory();
        using var client = factory.CreateAuthenticatedClient(7101, role);
        var response = await client.PostAsJsonAsync($"/api/v1/admin/{route}", new
        {
            fullName = "New Account",
            email = "new@example.test",
            phoneNumber = "0901234567",
            businessName = "Business",
            category = "Hotel",
            linkedPoiIds = new[] { 1L },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExistingTokenIsBlockedAsSoonAsPersistedPasswordFlagIsSet()
    {
        using var factory = Factory();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Id = 7102,
                FullName = "Staff",
                Email = "gate@example.test",
                Role = UserRole.Staff,
                Status = AccountStatus.Active
            });
            await db.SaveChangesAsync();
            return true;
        });
        using var client = factory.CreateAuthenticatedClient(7102, UserRole.Staff);
        var path = "/api/v1/admin/service-provider-accounts/available-pois";
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.FindAsync(7102L))!.MustChangePassword = true;
            await db.SaveChangesAsync();
            return true;
        });
        var blocked = await client.GetAsync(path);
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("auth.password_change_required");
    }

    private static TripMateApiFactory Factory() => new(configureTestServices: services =>
    {
        services.RemoveAll<IAccountProvisioningEmailSender>();
        services.AddSingleton<IAccountProvisioningEmailSender, DeliveredEmailSender>();
        services.RemoveAll<IAccountProvisioningLock>();
        services.AddSingleton<IAccountProvisioningLock, NoOpProvisioningLock>();
    });

    private static async Task Seed(TripMateApiFactory factory, long id, UserRole role, bool mustChangePassword)
    {
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Id = id,
                FullName = "Acting account",
                Email = $"actor-{id}@example.test",
                Role = role,
                Status = AccountStatus.Active,
                MustChangePassword = mustChangePassword,
                PasswordHash = new TripMate.Infrastructure.Services.PasswordHasherService().Hash("TempPass1!")
            });
            await db.SaveChangesAsync();
            return true;
        });
    }

    private sealed class DeliveredEmailSender : IAccountProvisioningEmailSender
    {
        public Task<EmailDeliveryResult> SendTemporaryPasswordAsync(string email, string temporaryPassword,
            CancellationToken cancellationToken) => Task.FromResult(EmailDeliveryResult.Delivered);
    }

    private sealed class NoOpProvisioningLock : IAccountProvisioningLock
    {
        public Task AcquireAsync(string normalizedEmail, string normalizedPhone, IReadOnlyCollection<long> poiIds,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}