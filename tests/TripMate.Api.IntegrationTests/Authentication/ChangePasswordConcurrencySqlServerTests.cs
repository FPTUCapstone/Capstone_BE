using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Authentication;

/// <summary>
/// UC-07 concurrency: two requests submitting the same current password for one account must
/// not both succeed, and the losing request must never report success with a session the
/// winning request's transaction has already revoked. The lock (sp_getapplock, scoped to the
/// transaction) is enforced by SQL Server itself, so this test uses two entirely separate
/// TripMateApiFactory instances — two independent DI containers/processes, each with its own
/// IAccountCredentialLock instance — against the same real SQL Server database. A lock that only
/// serialized within one process (e.g. an in-memory semaphore) would not serialize these two.
/// </summary>
public sealed class ChangePasswordConcurrencySqlServerTests
{
    private const string CurrentPassword = "CurrentPass1!";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentChangePasswordRequests_AcrossTwoSeparateApiProcesses_ExactlyOneSucceedsAndNeverRevokesTheWinnersSession()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var userId = await SeedUserAsync(database);

        using var firstProcessFactory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var secondProcessFactory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var firstClient = firstProcessFactory.CreateAuthenticatedClient(userId, UserRole.Traveler);
        using var secondClient = secondProcessFactory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync(
                "/api/v1/auth/change-password",
                new { currentPassword = CurrentPassword, newPassword = "NewPassA1!" }),
            secondClient.PostAsJsonAsync(
                "/api/v1/auth/change-password",
                new { currentPassword = CurrentPassword, newPassword = "NewPassB1!" }));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1,
            "two concurrent attempts with the same current password must not both succeed");
        var rejected = responses.Single(response => response.StatusCode == HttpStatusCode.BadRequest);
        var rejectedJson = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        rejectedJson.GetProperty("errorCode").GetString().Should().Be("MSG17",
            "the losing request re-reads the already-changed hash and correctly fails as a wrong current password, never as a mid-flight conflict");

        var winner = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
        var winnerData = (await winner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var winnerRefreshToken = winnerData.GetProperty("refreshToken").GetString()!;

        await using var verification = database.CreateDbContext();
        var tokens = await verification.RefreshTokens.Where(t => t.UserId == userId).ToListAsync();
        tokens.Count(t => t.RevokedAtUtc == null).Should().Be(1,
            "exactly one active session remains — the winner's");

        var activeToken = tokens.Single(t => t.RevokedAtUtc == null);
        activeToken.TokenHash.Should().Be(HashRefreshToken(winnerRefreshToken),
            "the still-active row must be the winner's own token, not a different one left active by accident");
    }

    private static async Task<long> SeedUserAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var hasher = new PasswordHasherService();
        var user = new User
        {
            Email = "concurrent-change-password@example.com",
            FullName = "Sql Test User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            PasswordHash = hasher.Hash(CurrentPassword),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        context.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = $"hashed:seed-{Guid.NewGuid()}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
        });
        await context.SaveChangesAsync();

        return user.Id;
    }

    // Matches IJwtTokenService.HashRefreshToken's production algorithm (SHA-256, base64) without
    // needing a fully configured JwtTokenService (its JwtOptions are irrelevant to this method).
    private static string HashRefreshToken(string rawRefreshToken) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawRefreshToken)));
}