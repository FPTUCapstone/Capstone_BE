using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public sealed class EmailVerificationResendEndpointsTests
{
    [Fact]
    public async Task PendingAccount_WithValidCredentials_SendsBackendGeneratedLink()
    {
        var sender = new RecordingSender();
        using var factory = CreateFactory(sender);
        using var client = factory.CreateClient();
        await Seed(factory, AccountStatus.PendingEmailVerification);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/web/resend-verification",
            new { email = " PENDING@example.com ", password = "CorrectPass1" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("messageCode").GetString()
            .Should().Be("MSG_RESEND_SUCCESS");
        sender.Deliveries.Should().ContainSingle().Which.Should().Be(("pending@example.com", "https://firebase.test/action"));
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task WrongPassword_ReturnsUnauthorized_AndSendsNothing()
    {
        var sender = new RecordingSender();
        using var factory = CreateFactory(sender);
        using var client = factory.CreateClient();
        await Seed(factory, AccountStatus.PendingEmailVerification);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/web/resend-verification",
            new { email = "pending@example.com", password = "WrongPass1" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        sender.Deliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task ActiveAccount_ReturnsConflict_AndSendsNothing()
    {
        var sender = new RecordingSender();
        using var factory = CreateFactory(sender);
        using var client = factory.CreateClient();
        await Seed(factory, AccountStatus.Active);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/web/resend-verification",
            new { email = "pending@example.com", password = "CorrectPass1" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        sender.Deliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task SecondSuccessfulRequestWithinSixtySeconds_ReturnsTooManyRequests()
    {
        var sender = new RecordingSender();
        using var factory = CreateFactory(sender);
        using var client = factory.CreateClient();
        await Seed(factory, AccountStatus.PendingEmailVerification);
        var body = new { email = "pending@example.com", password = "CorrectPass1" };

        (await client.PostAsJsonAsync("/api/v1/auth/web/resend-verification", body)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/v1/auth/web/resend-verification", body)).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests);
        sender.Deliveries.Should().ContainSingle();
    }

    private static TripMateApiFactory CreateFactory(RecordingSender sender) => new(
        configureTestServices: services =>
        {
            services.RemoveAll<IEmailVerificationLinkService>();
            services.RemoveAll<IEmailVerificationSender>();
            services.AddSingleton<IEmailVerificationLinkService>(new StubLinkService());
            services.AddSingleton<IEmailVerificationSender>(sender);
        });

    private static Task<bool> Seed(TripMateApiFactory factory, AccountStatus status) =>
        factory.WithDbContextAsync(async db =>
        {
            using var scope = factory.Services.CreateScope();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            db.Users.Add(new User
            {
                Email = "pending@example.com",
                FullName = "Pending User",
                Role = UserRole.Traveler,
                Status = status,
                PasswordHash = hasher.Hash("CorrectPass1"),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return true;
        });

    private sealed class StubLinkService : IEmailVerificationLinkService
    {
        public Task<string> GenerateAsync(string email, CancellationToken cancellationToken) =>
            Task.FromResult("https://firebase.test/action");
    }

    private sealed class RecordingSender : IEmailVerificationSender
    {
        public List<(string Email, string Link)> Deliveries { get; } = [];

        public Task<EmailDeliveryResult> SendAsync(
            string destinationEmail,
            string verificationLink,
            CancellationToken cancellationToken)
        {
            Deliveries.Add((destinationEmail, verificationLink));
            return Task.FromResult(EmailDeliveryResult.Delivered);
        }
    }
}
