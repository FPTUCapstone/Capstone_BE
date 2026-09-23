using System.Text;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.SignOut;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

/// <summary>
/// Uses SQL Server because EF InMemory cannot prove first-write-wins update semantics.
/// Skipped unless TRIPMATE_SQLSERVER_TEST_CONNECTION is configured.
/// </summary>
public sealed class SignOutSqlServerConcurrencyTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_ConcurrentCurrentSessionLogout_FirstRevocationTimestampWins()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var jwt = new StubJwtTokenService();
        const string rawToken = "concurrent-current-session";

        await using (var seed = database.CreateDbContext())
        {
            var user = new User
            {
                Email = "logout.race@example.com",
                FullName = "Logout Race User",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                PasswordHash = "hashed:x",
                CreatedAtUtc = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero),
                UpdatedAtUtc = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero),
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            seed.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = jwt.HashRefreshToken(rawToken),
                CreatedAtUtc = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero),
                ExpiresAtUtc = new DateTimeOffset(2026, 9, 27, 8, 0, 0, TimeSpan.Zero),
            });
            await seed.SaveChangesAsync();
        }

        var firstTimestamp = new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
        var secondTimestamp = firstTimestamp.AddMinutes(1);
        await using var firstContext = database.CreateDbContext();
        await using var secondContext = database.CreateDbContext();
        var firstHandler = new SignOutCommandHandler(
            firstContext,
            jwt,
            new FixedDateTimeProvider(firstTimestamp));
        var secondHandler = new SignOutCommandHandler(
            secondContext,
            jwt,
            new FixedDateTimeProvider(secondTimestamp));
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = RunAfterGateAsync(firstHandler, startGate.Task, rawToken, "trace-first");
        var secondTask = RunAfterGateAsync(secondHandler, startGate.Task, rawToken, "trace-second");
        startGate.SetResult(true);

        var results = await Task.WhenAll(firstTask, secondTask);

        results.Should().OnlyContain(result => result.IsSuccess);
        await using var verification = database.CreateDbContext();
        var session = await verification.RefreshTokens.AsNoTracking().SingleAsync();
        var audits = await verification.AuditLogs.AsNoTracking().ToListAsync();
        var winningAudit = audits.Should().ContainSingle(
            "only the request that changes the session receives a security audit event")
            .Subject;
        session.RevokedAtUtc.Should().Be(winningAudit.CreatedAtUtc,
            "the zero-row loser must not overwrite the timestamp stored by the winning update");
    }

    private static async Task<Result<bool>> RunAfterGateAsync(
        SignOutCommandHandler handler,
        Task startGate,
        string rawToken,
        string traceId)
    {
        await startGate;
        return await handler.Handle(
            new SignOutCommand(rawToken, "Mobile", traceId),
            CancellationToken.None);
    }

    private sealed class StubJwtTokenService : IJwtTokenService
    {
        public (string Token, DateTimeOffset ExpiresAtUtc) GenerateAccessToken(User user) =>
            throw new NotSupportedException();

        public string GenerateRefreshToken() => throw new NotSupportedException();

        public string HashRefreshToken(string rawRefreshToken)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(rawRefreshToken)));
        }
    }

    private sealed class FixedDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}