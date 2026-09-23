using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Api.IntegrationTests.Authentication;

/// <summary>
/// Task 7: end-to-end UC-06 flow against a real SQL Server database (existing schema v7
/// only): request → state → email → confirm → password_hash update + refresh-token
/// revocation inside the existing-schema transaction, including forced-failure rollback.
/// These tests skip unless TRIPMATE_SQLSERVER_TEST_CONNECTION is configured.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class PasswordResetSqlServerTests
{
    private const string ForcedFailureHashPrefix = "FORCED-FAILURE";

    private sealed class FakeEmailSender : IEmailSender
    {
        public string? LastOtp { get; private set; }

        public Task<EmailDeliveryResult> SendPasswordResetOtpAsync(
            string destinationEmail,
            string otp,
            CancellationToken cancellationToken)
        {
            LastOtp = otp;
            return Task.FromResult(EmailDeliveryResult.Delivered);
        }
    }

    private sealed class ForcedFailurePasswordHasher : IPasswordHasherService
    {
        public string Hash(string password) => $"{ForcedFailureHashPrefix}-HASH";

        public bool Verify(string password, string passwordHash) =>
            !passwordHash.StartsWith(ForcedFailureHashPrefix, StringComparison.Ordinal);
    }

    private sealed class CoordinatingPasswordHasher : IPasswordHasherService, IDisposable
    {
        private readonly ManualResetEventSlim secondHashCallEntered = new(false);
        private int hashCalls;

        public int HashCalls => Volatile.Read(ref hashCalls);

        public string Hash(string password)
        {
            if (Interlocked.Increment(ref hashCalls) == 1)
            {
                // On the vulnerable path both independent requests pass reset-state
                // verification and reach hashing together. Once per-account serialization
                // is present, this bounded wait expires and only the winner reaches hashing.
                secondHashCallEntered.Wait(TimeSpan.FromSeconds(2));
            }
            else
            {
                secondHashCallEntered.Set();
            }

            return $"concurrent-hash:{password}";
        }

        public bool Verify(string password, string passwordHash) =>
            passwordHash == $"concurrent-hash:{password}";

        public void Dispose() => secondHashCallEntered.Dispose();
    }

    private static async Task<long> SeedUserAsync(
        SqlServerTestDatabase database,
        string email)
    {
        await using var context = database.CreateDbContext();
        var user = new User
        {
            Email = email,
            FullName = "Sql Test User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            PasswordHash = "existing-password-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        context.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            User = user,
            TokenHash = $"hash-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
        });
        context.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            User = user,
            TokenHash = $"hash-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
        });
        await context.SaveChangesAsync();
        return user.Id;
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task EndToEndConfirm_UpdatesPasswordHashAndRevokesAllActiveTokens()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var email = "e2e@example.com";
        var userId = await SeedUserAsync(database, email);
        var sender = new FakeEmailSender();
        using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            emailSenderFactory: _ => sender);
        using var client = factory.CreateClient();

        var requestResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/password-reset/request", new { email });
        requestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var otp = sender.LastOtp!;

        var confirmResponse = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm",
            new { email, code = otp, newPassword = "NewPassword1!" });

        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await confirmResponse.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("message").GetString().Should()
            .Be("Your password has been reset. You can now sign in with your new password.");

        // Verify with a brand-new context: the committed transaction is authoritative.
        await using var verificationContext = database.CreateDbContext();
        var user = await verificationContext.Users.FindAsync(userId);
        user!.PasswordHash.Should().NotBe("existing-password-hash");
        var tokens = await verificationContext.RefreshTokens.ToListAsync();
        tokens.Should().HaveCount(2);
        tokens.Should().OnlyContain(t => t.RevokedAtUtc != null);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentConfirm_WithIndependentScopes_AllowsExactlyOneSuccess()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var email = "concurrent-confirm@example.com";
        var userId = await SeedUserAsync(database, email);
        var sender = new FakeEmailSender();
        using var hasher = new CoordinatingPasswordHasher();
        using var factory = new TripMateApiFactory(
                sqlServerConnectionString: database.ConnectionString,
                emailSenderFactory: _ => sender)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPasswordHasherService>();
                services.AddSingleton<IPasswordHasherService>(hasher);
            }));
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();

        var requestResponse = await firstClient.PostAsJsonAsync(
            "/api/v1/auth/password-reset/request", new { email });
        requestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var otp = sender.LastOtp!;
        var confirmBody = new { email, code = otp, newPassword = "NewPassword1!" };

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", confirmBody),
            secondClient.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", confirmBody));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        var rejected = responses.Single(response => response.StatusCode == HttpStatusCode.BadRequest);
        var rejectedJson = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        rejectedJson.GetProperty("errorCode").GetString().Should().Be("MSG14");
        hasher.HashCalls.Should().Be(1);

        var replayResponse = await firstClient.PostAsJsonAsync(
            "/api/v1/auth/password-reset/confirm", confirmBody);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var replayJson = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();
        replayJson.GetProperty("errorCode").GetString().Should().Be("MSG14");

        await using var verificationContext = database.CreateDbContext();
        var user = await verificationContext.Users.FindAsync(userId);
        user!.PasswordHash.Should().Be("concurrent-hash:NewPassword1!");
        var tokens = await verificationContext.RefreshTokens.ToListAsync();
        tokens.Should().OnlyContain(token => token.RevokedAtUtc != null);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ForcedPasswordHashFailure_RollsBackPasswordAndTokenMutation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var email = "rollback@example.com";
        var userId = await SeedUserAsync(database, email);
        await database.ExecuteNonQueryAsync($"""
            ALTER TABLE dbo.Users
            ADD CONSTRAINT CK_Users_ResetForcedFailTest
            CHECK (password_hash NOT LIKE '{ForcedFailureHashPrefix}%');
            """);
        var sender = new FakeEmailSender();
        using var factory = new TripMateApiFactory(
                sqlServerConnectionString: database.ConnectionString,
                emailSenderFactory: _ => sender)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPasswordHasherService>();
                services.AddSingleton<IPasswordHasherService>(new ForcedFailurePasswordHasher());
            }));
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email });
        var otp = sender.LastOtp!;

        var confirmResponse = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm",
            new { email, code = otp, newPassword = "NewPassword1!" });

        confirmResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var json = await confirmResponse.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("MSG127");

        // No partial mutation: both the password hash and the token revocation rolled back.
        await using var verificationContext = database.CreateDbContext();
        var user = await verificationContext.Users.FindAsync(userId);
        user!.PasswordHash.Should().Be("existing-password-hash");
        var tokens = await verificationContext.RefreshTokens.ToListAsync();
        tokens.Should().OnlyContain(t => t.RevokedAtUtc == null);
    }
}