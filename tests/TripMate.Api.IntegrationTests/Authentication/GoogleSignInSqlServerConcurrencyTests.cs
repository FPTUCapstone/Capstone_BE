using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Authentication.GoogleAuth;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

/// <summary>
/// UC-04 spec §4.2-B7 (BR-02 race): two concurrent Google sign-ins with the same unknown
/// email race the auto-provision insert. The unique index <c>UX_Users_Email</c> arbitrates —
/// the losing request must catch the violation, re-load the provisioned account, fall through
/// the same status gates and still succeed (never 500, never a duplicate user).
///
/// Requires a real SQL Server: InMemory does not enforce unique indexes nor arbitrate
/// concurrent inserts. Skipped unless `TRIPMATE_SQLSERVER_TEST_CONNECTION` is set.
/// </summary>
public sealed class GoogleSignInSqlServerConcurrencyTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_ConcurrentProvisionSameEmail_ExactlyOneUserAndBothRequestsSucceed()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        var firebase = new StubFirebaseService();
        var jwt = new StubJwtTokenService();
        var clock = new FixedDateTimeProvider(new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero));

        var command = new GoogleAuthCommand("valid-google-id-token");
        var handler1 = CreateHandler(database, firebase, jwt, clock);
        var handler2 = CreateHandler(database, firebase, jwt, clock);

        // Fire both concurrently: the second request's SaveChanges hits UX_Users_Email while
        // the first transaction is still committing — the race path must adopt the account.
        var task1 = Task.Run(() => handler1.Handle(command, CancellationToken.None));
        var task2 = Task.Run(() => handler2.Handle(command, CancellationToken.None));
        var results = await Task.WhenAll(task1, task2);

        results.Should().OnlyContain(r => r.IsSuccess,
            "the unique index arbitrates the race; the losing request adopts the provisioned account");

        await using var verification = database.CreateDbContext();
        var users = await verification.Users.AsNoTracking()
            .Where(u => u.Email == "race.user@gmail.com")
            .ToListAsync();

        users.Should().ContainSingle("UX_Users_Email prevents a duplicate provisioned account");

        var provisioned = users.Single();
        provisioned.Role.Should().Be(UserRole.Traveler, "BR-02: auto-provisioning only ever creates a Traveler");
        provisioned.Status.Should().Be(AccountStatus.Active);
        provisioned.LastLoginAtUtc.Should().NotBeNull("P2a: the user signs in at provisioning time");

        // Both requests end up holding a session for the SAME account.
        results.Select(r => r.Value.UserId)
            .Should().OnlyContain(id => id == provisioned.Id);

        var refreshRows = await verification.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == provisioned.Id)
            .ToListAsync();
        refreshRows.Should().HaveCount(2, "each successful request persists its own session row");
    }

    private static GoogleAuthCommandHandler CreateHandler(
        SqlServerTestDatabase database,
        IFirebaseAuthService firebase,
        IJwtTokenService jwt,
        IDateTimeProvider clock) =>
        new(
            database.CreateDbContext(),
            firebase,
            jwt,
            clock,
            NullLogger<GoogleAuthCommandHandler>.Instance);

    private sealed class StubFirebaseService : IFirebaseAuthService
    {
        private readonly FirebaseTokenValidationResult _result =
            new("fb-uid-race", "race.user@gmail.com", true, "Race User", null, "google.com");

        public Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(
            string idToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed class StubJwtTokenService : IJwtTokenService
    {
        private readonly DateTimeOffset _expiresAt =
            new(2026, 9, 13, 11, 0, 0, TimeSpan.Zero);

        public string GenerateRefreshToken() =>
            Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        public string HashRefreshToken(string refreshToken)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(refreshToken)));
        }

        public (string Token, DateTimeOffset ExpiresAtUtc) GenerateAccessToken(User user) =>
            ("stub-access-token", _expiresAt);
    }

    private sealed class FixedDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}