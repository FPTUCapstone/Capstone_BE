using System.Net;

using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.Common;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public class PasswordResetEndpointsTests
{
    private const string GenericMessage =
        "If an account exists for this email, reset instructions have been sent.";
    private const string SuccessMessage =
        "Your password has been reset. You can now sign in with your new password.";

    private sealed class FakeEmailSender : IEmailSender
    {
        public EmailDeliveryStatus DeliveryStatusToReturn { get; set; } = EmailDeliveryStatus.Delivered;

        public string? LastDestination { get; private set; }

        public string? LastOtp { get; private set; }

        public Task<EmailDeliveryResult> SendPasswordResetOtpAsync(
            string destinationEmail,
            string otp,
            CancellationToken cancellationToken)
        {
            LastDestination = destinationEmail;
            LastOtp = otp;
            return Task.FromResult(DeliveryStatusToReturn == EmailDeliveryStatus.Delivered
                ? EmailDeliveryResult.Delivered
                : DeliveryStatusToReturn == EmailDeliveryStatus.DefiniteFailure
                    ? EmailDeliveryResult.DefiniteFailure
                    : EmailDeliveryResult.Unknown);
        }
    }

    private static (TripMateApiFactory Factory, FakeEmailSender Sender) CreateFactory()
    {
        var sender = new FakeEmailSender();
        var factory = new TripMateApiFactory(emailSenderFactory: _ => sender);
        return (factory, sender);
    }

    private async Task SeedUserAsync(
        TripMateApiFactory factory,
        string email,
        AccountStatus status = AccountStatus.Active,
        string? passwordHash = "hashed:OldPassword1!")
    {
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Email = email,
                FullName = "Test User",
                Role = UserRole.Traveler,
                Status = status,
                PasswordHash = passwordHash,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
            return true;
        });
    }

    [Fact(DisplayName = "PLAN-API-01: request for eligible account returns the generic 200 payload")]
    public async Task Request_EligibleAccount_ReturnsGeneric200()
    {
        var (factory, sender) = CreateFactory();
        using var _ = factory;
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "user@example.com");

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "user@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("message").GetString().Should().Be(GenericMessage);
        sender.LastOtp.Should().NotBeNullOrEmpty();
        sender.LastOtp!.Length.Should().Be(6);
    }

    [Fact(DisplayName = "PLAN-API-02: unknown email returns the exact same generic 200 payload")]
    public async Task Request_UnknownEmail_ReturnsIdenticalGeneric200()
    {
        var (factory, sender) = CreateFactory();
        using var _ = factory;
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "user@example.com");

        var known = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "user@example.com" });
        var otpAfterKnownRequest = sender.LastOtp;
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "unknown@example.com" });

        unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        var knownBody = await known.Content.ReadAsStringAsync();
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        unknownBody.Should().Be(knownBody);
        sender.LastOtp.Should().Be(otpAfterKnownRequest, "no new OTP may be generated for an unknown email");
    }

    [Fact(DisplayName = "PLAN-API-03: Google-only account returns the same generic 200 with no email")]
    public async Task Request_GoogleOnly_ReturnsGeneric200WithoutEmail()
    {
        var (factory, sender) = CreateFactory();
        using var _ = factory;
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "google@example.com", passwordHash: null);

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "google@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()
            .Should().Be(GenericMessage);
        sender.LastOtp.Should().BeNull();
    }

    [Theory(DisplayName = "PLAN-API-04: Locked/Inactive accounts return the same generic 200")]
    [InlineData(AccountStatus.Locked)]
    [InlineData(AccountStatus.Inactive)]
    public async Task Request_LockedOrInactive_ReturnsGeneric200(AccountStatus status)
    {
        var (factory, sender) = CreateFactory();
        using var _ = factory;
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "locked@example.com", status: status);

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "locked@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()
            .Should().Be(GenericMessage);
        sender.LastOtp.Should().BeNull();
    }

    [Fact(DisplayName = "PLAN-API-05: resend during cooldown returns the same generic 200 with no new email")]
    public async Task Request_DuringCooldown_ReturnsGeneric200WithoutNewEmail()
    {
        var (factory, sender) = CreateFactory();
        using var _ = factory;
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "user@example.com");

        await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "user@example.com" });
        var otpAfterFirst = sender.LastOtp;
        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "user@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()
            .Should().Be(GenericMessage);
        sender.LastOtp.Should().Be(otpAfterFirst);
    }

    [Fact(DisplayName = "PLAN-API-06: definite delivery failure and unknown delivery return the same generic 200")]
    public async Task Request_WhenDeliveryFailsOrIsUnknown_ReturnsGeneric200()
    {
        foreach (var deliveryStatus in new[]
                 {
                     EmailDeliveryStatus.DefiniteFailure,
                     EmailDeliveryStatus.Unknown,
                 })
        {
            var (factory, sender) = CreateFactory();
            sender.DeliveryStatusToReturn = deliveryStatus;
            using var _ = factory;
            using var client = factory.CreateClient();
            await SeedUserAsync(factory, "user@example.com");

            var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "user@example.com" });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("message").GetString().Should().Be(GenericMessage);
            json.EnumerateObject().Should().ContainSingle(property => property.Name == "message");
        }
    }

    [Fact(DisplayName = "PLAN-API-07: malformed email returns ValidationProblemDetails")]
    public async Task Request_MalformedEmail_ReturnsValidationProblemDetails()
    {
        var (factory, _) = CreateFactory();
        using var _ = factory;
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "not-an-email" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
    }

    [Fact(DisplayName = "PLAN-API-08: confirm with the delivered OTP resets the password and revokes tokens")]
    public async Task Confirm_WithDeliveredOtp_ResetsPassword()
    {
        var (factory, sender) = CreateFactory();
        using var factoryOwner = factory;
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "user@example.com");
        var userId = await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == "user@example.com");
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                User = user,
                TokenHash = "hash-1",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
            return user.Id;
        });

        await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "user@example.com" });
        var otp = sender.LastOtp!;

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm",
            new { email = "user@example.com", code = otp, newPassword = "NewPassword1!" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("message").GetString().Should().Be(SuccessMessage);
        json.TryGetProperty("data", out _).Should().BeFalse("UC-06 uses direct DTOs, no legacy envelope");

        var passwordHash = await factory.WithDbContextAsync(db =>
            db.Users.Where(u => u.Email == "user@example.com").Select(u => u.PasswordHash).SingleAsync());
        passwordHash.Should().NotBe("hashed:OldPassword1!");
        var revoked = await factory.WithDbContextAsync(db =>
            db.RefreshTokens.Where(t => t.UserId == userId).Select(t => t.RevokedAtUtc).SingleAsync());
        revoked.Should().NotBeNull();
    }

    [Fact(DisplayName = "PLAN-API-09: bad OTP returns 400 ProblemDetails with MSG14")]
    public async Task Confirm_WithBadOtp_Returns400Msg14()
    {
        var (factory, sender) = CreateFactory();
        using var _ = factory;
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "user@example.com");
        await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "user@example.com" });

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm",
            new { email = "user@example.com", code = "000000", newPassword = "NewPassword1!" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("MSG14");
    }

    [Fact(DisplayName = "PLAN-API-10: expired OTP returns 400 MSG14")]
    public async Task Confirm_WithExpiredOtp_Returns400Msg14()
    {
        var sender1 = new FakeEmailSender();
        var fixedTime = new FixedDateTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new TripMateApiFactory(
            emailSenderFactory: _ => sender1,
            dateTimeProviderFactory: _ => fixedTime);
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "user@example.com");

        await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "user@example.com" });
        fixedTime.UtcNow = fixedTime.UtcNow.AddMinutes(4);

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm",
            new { email = "user@example.com", code = sender1.LastOtp!, newPassword = "NewPassword1!" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("MSG14");
    }

    [Fact(DisplayName = "PLAN-API-11: replaying a used OTP returns 400 MSG14")]
    public async Task Confirm_ReplayedOtp_Returns400Msg14()
    {
        var (factory, sender) = CreateFactory();
        using var _ = factory;
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "user@example.com");
        await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "user@example.com" });
        var otp = sender.LastOtp!;
        await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm",
            new { email = "user@example.com", code = otp, newPassword = "NewPassword1!" });

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm",
            new { email = "user@example.com", code = otp, newPassword = "AnotherPass1!" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("MSG14");
    }

    [Fact(DisplayName = "PLAN-API-12: invalid newPassword returns ValidationProblemDetails")]
    public async Task Confirm_WithInvalidPassword_ReturnsValidationProblemDetails()
    {
        var (factory, sender) = CreateFactory();
        using var _ = factory;
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "user@example.com");
        await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "user@example.com" });

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm",
            new { email = "user@example.com", code = sender.LastOtp!, newPassword = "short" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
    }

    [Fact(DisplayName = "PLAN-API-13: per-IP rate limiter returns 429 independent of account state")]
    public async Task Request_ExceedingPerIpLimit_Returns429()
    {
        var (factory, _) = CreateFactory();
        using var _ = factory;
        using var client = factory.CreateClient();
        await SeedUserAsync(factory, "user@example.com");

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request",
                new { email = $"attempt{attempt}@example.com" });
            statuses.Add(response.StatusCode);
        }

        statuses.Take(PasswordResetRateLimiter.PermitLimit)
            .Should().OnlyContain(s => s == HttpStatusCode.OK);
        statuses.Skip(PasswordResetRateLimiter.PermitLimit)
            .Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
    }

    private sealed class FixedDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}