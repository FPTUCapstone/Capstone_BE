using System.Data;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Contracts;

public sealed class OperatorContractChallengeLockSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Challenge_update_lock_serializes_verification_transactions()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var challengeId = Guid.NewGuid();
        await database.ExecuteNonQueryAsync($"EXEC('CREATE SCHEMA admin'); CREATE TABLE admin.OperatorContractSigningChallenges(challenge_id UNIQUEIDENTIFIER PRIMARY KEY); INSERT admin.OperatorContractSigningChallenges VALUES('{challengeId:D}');");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(database.ConnectionString).Options;
        await using var first = new ApplicationDbContext(options);
        await using var second = new ApplicationDbContext(options);
        await using var firstTransaction = await first.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await first.LockOperatorContractChallengeAsync(challengeId, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var secondLock = second.LockOperatorContractChallengeAsync(challengeId, timeout.Token);
        await Task.Delay(200, timeout.Token);
        Assert.False(secondLock.IsCompleted, "A competing verification must wait until the current challenge verifier commits.");
        await firstTransaction.CommitAsync(timeout.Token);
        await secondLock.WaitAsync(TimeSpan.FromSeconds(5));
        await secondTransaction.RollbackAsync(timeout.Token);
    }

    [Fact]
    public async Task Challenge_lock_requires_an_explicit_transaction()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(new SqlConnection()).Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LockOperatorContractChallengeAsync(Guid.NewGuid(), default));
    }
}