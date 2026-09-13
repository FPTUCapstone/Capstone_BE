using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

/// <summary>
/// UC-04 S11: authentication endpoints must never write passwords, Firebase ID tokens or
/// TripMate access/refresh tokens to the log output.
///
/// The test drives real auth traffic with marked secrets (a wrong-password 401, a successful
/// 200 login that returns a real access token, and a Google call carrying a fake Firebase ID
/// token), then inspects the Serilog file-sink output captured via
/// `appsettings.Testing.json`. The log MUST contain the request outcomes (proving logging
/// works) but MUST NOT contain any of the secrets.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public class LogSanitizationTests
{
    private const string LogRelativePath = "logs/uc04-s11.log";
    private const string Password = "S3cretPass1!x";
    private const string WrongPassword = "WrongPass9!";
    private const string FakeGoogleIdToken = "fake-google-id-token-do-not-log-abc123";

    private static string LogFullPath =>
        Path.Combine(Directory.GetCurrentDirectory(), LogRelativePath);

    private static void ResetLogFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFullPath)!);
        if (File.Exists(LogFullPath))
        {
            File.Delete(LogFullPath);
        }
    }

    private static async Task<(string AccessToken, long UserId)> SeedAndLoginAsync(
        TripMateApiFactory factory,
        HttpClient client,
        string email,
        string password)
    {
        // Seed an Active user whose password hash matches the marked password.
        await factory.WithDbContextAsync(async db =>
        {
            string hash;
            using (var scope = factory.Services.CreateScope())
            {
                var hasher = scope.ServiceProvider
                    .GetRequiredService<IPasswordHasherService>();
                hash = hasher.Hash(password);
            }

            db.Users.Add(new TripMate.Domain.Entities.User
            {
                Email = email,
                FullName = "S11 Test User",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                PasswordHash = hash,
            });
            await db.SaveChangesAsync();
            return true;
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email,
            password,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        return (
            data.GetProperty("accessToken").GetString()!,
            data.GetProperty("userId").GetInt64());
    }

    [Fact]
    public async Task AuthTraffic_NeverLogsPasswordsOrTokens()
    {
        ResetLogFile();

        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        // 1. Failed login → 401 (request body carries the marked password).
        var failed = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "s11.user@example.com",
            password = WrongPassword,
        });
        failed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 2. Successful login → 200 (response carries a real access token).
        var (accessToken, _) = await SeedAndLoginAsync(
            factory, client, "s11.user@example.com", Password);
        accessToken.Should().NotBeNullOrWhiteSpace();

        // 3. Google call carrying a marked fake Firebase ID token → 503 in Testing
        //    (Firebase unconfigured → fail closed), token present in the request body.
        var google = await client.PostAsJsonAsync("/api/v1/auth/google", new
        {
            idToken = FakeGoogleIdToken,
        });
        google.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Allow the Serilog file sink to flush, then inspect the captured output.
        // The sink keeps the file open for writing, so reads must share write access.
        var logged = string.Empty;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(300);
            if (!File.Exists(LogFullPath))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(
                    LogFullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                logged = await reader.ReadToEndAsync();
            }
            catch (IOException)
            {
                // Sink still flushing — retry on the next attempt.
            }

            // Non-vacuous guard: the sink must have captured the request outcomes —
            // otherwise the test would pass without proving that logging works.
            if (logged.Contains("/api/v1/auth/login"))
            {
                break;
            }
        }

        logged.Should().Contain("/api/v1/auth/login",
            $"log file: {LogFullPath}; length: {logged.Length}; head: {logged[..Math.Min(400, logged.Length)]}");

        // S11: none of the marked secrets may appear anywhere in the log output.
        logged.Should().NotContain(Password);
        logged.Should().NotContain(WrongPassword);
        logged.Should().NotContain(accessToken);
        logged.Should().NotContain(FakeGoogleIdToken);
        logged.Should().NotContain("eyJhbGci", "no JWT-shaped string may be logged");
    }
}
