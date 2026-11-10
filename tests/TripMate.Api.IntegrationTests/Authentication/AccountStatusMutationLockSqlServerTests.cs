using System.Diagnostics;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Locks;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Authentication;

public sealed class AccountStatusMutationLockSqlServerTests
{
    [Fact]
    public void Contract_UsesApprovedPrincipalBudgetAndCommandMargin()
    {
        SqlServerApplicationLockContract.DatabasePrincipal.Should().Be("TripMateAppLockRole");
        SqlServerApplicationLockContract.TotalBudgetMilliseconds.Should().Be(15_000);
        SqlServerApplicationLockContract.CommandTimeoutSeconds.Should().BeGreaterThan(15);
    }

    [Theory]
    [InlineData(-1, TransactionLockFailureKind.LockTimeout)]
    [InlineData(-2, TransactionLockFailureKind.LockCanceledByServer)]
    [InlineData(-3, TransactionLockFailureKind.DeadlockVictim)]
    [InlineData(-999, TransactionLockFailureKind.LockProtocolError)]
    [InlineData(-42, TransactionLockFailureKind.ProviderFailure)]
    public void ResultMapping_MapsEveryNegativeReturn(int result, TransactionLockFailureKind expected)
    {
        var action = () => SqlServerTransactionApplicationLockExecutor.EnsureAcquired(
            result,
            "AccountStatus",
            CancellationToken.None);

        action.Should().Throw<TransactionLockAcquisitionException>()
            .Which.FailureKind.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ResultMapping_AcceptsSuccessfulResults(int result)
    {
        var action = () => SqlServerTransactionApplicationLockExecutor.EnsureAcquired(
            result,
            "AccountStatus",
            CancellationToken.None);

        action.Should().NotThrow();
    }

    [Fact]
    public void ResultMapping_RejectsUnexpectedPositiveResult()
    {
        var action = () => SqlServerTransactionApplicationLockExecutor.EnsureAcquired(
            2,
            "AccountStatus",
            CancellationToken.None);

        action.Should().Throw<TransactionLockAcquisitionException>()
            .Which.FailureKind.Should().Be(TransactionLockFailureKind.LockProtocolError);
    }

    [Fact]
    public void ResultMapping_WhenServerReturnsCanceledAndTokenIsCanceled_ThrowsCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var action = () => SqlServerTransactionApplicationLockExecutor.EnsureAcquired(
            -2,
            "AccountStatus",
            source.Token);

        action.Should().Throw<OperationCanceledException>();
    }

    [Theory]
    [InlineData(1205, TransactionLockFailureKind.DeadlockVictim)]
    [InlineData(-2, TransactionLockFailureKind.CommandTimeout)]
    [InlineData(10054, TransactionLockFailureKind.ProviderFailure)]
    public void SqlErrorMapping_SeparatesDeadlockCommandTimeoutAndProviderFailure(
        int number,
        TransactionLockFailureKind expected)
    {
        SqlServerTransactionApplicationLockExecutor.ClassifySqlError(number)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 15_000)]
    [InlineData(1, 14_999)]
    [InlineData(14_999, 1)]
    [InlineData(15_000, 0)]
    [InlineData(30_000, 0)]
    public void TotalBudget_IsMonotonicAndNeverResetsPerResource(
        long elapsedMilliseconds,
        int expectedRemaining)
    {
        SqlServerTransactionApplicationLockExecutor.RemainingBudgetMilliseconds(elapsedMilliseconds)
            .Should().Be(expectedRemaining);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task AcquireWithoutCallerTransaction_FailsBeforeSql()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await using var context = database.CreateDbContext();
        IAccountStatusMutationLock sut = new SqlServerAccountStatusMutationLock(
            new SqlServerTransactionApplicationLockExecutor(context));

        var action = () => sut.AcquireSharedAsync([1], CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*caller-owned transaction*");
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("Shared", "Shared", false)]
    [InlineData("Shared", "Exclusive", true)]
    [InlineData("Exclusive", "Shared", true)]
    [InlineData("Exclusive", "Exclusive", true)]
    public async Task TwoPrincipalsInApprovedRole_UseExpectedCompatibilityAndRollbackRelease(
        string firstMode,
        string secondMode,
        bool shouldBlock)
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ConfigurePrincipalsAsync(database);
        (await database.ExecuteScalarAsync<int>(
            "SELECT IS_ROLEMEMBER(N'TripMateAppLockRole', N'R1PrincipalA');"))
            .Should().Be(1);
        (await database.ExecuteScalarAsync<int>(
            "SELECT IS_ROLEMEMBER(N'TripMateAppLockRole', N'R1PrincipalB');"))
            .Should().Be(1);

        await using var first = database.CreateDbContext();
        await using var second = database.CreateDbContext();
        await first.Database.OpenConnectionAsync();
        await second.Database.OpenConnectionAsync();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        await first.Database.ExecuteSqlRawAsync("EXECUTE AS USER = N'R1PrincipalA';");
        await second.Database.ExecuteSqlRawAsync("EXECUTE AS USER = N'R1PrincipalB';");

        IAccountStatusMutationLock firstLock = new SqlServerAccountStatusMutationLock(
            new SqlServerTransactionApplicationLockExecutor(first));
        IAccountStatusMutationLock secondLock = new SqlServerAccountStatusMutationLock(
            new SqlServerTransactionApplicationLockExecutor(second));

        await AcquireAsync(firstLock, firstMode, [7], CancellationToken.None);

        if (!shouldBlock)
        {
            using var completes = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await AcquireAsync(secondLock, secondMode, [7], completes.Token);
            await firstTransaction.RollbackAsync();
            await secondTransaction.RollbackAsync();
            return;
        }

        using var blocked = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var blockedAction = () => AcquireAsync(secondLock, secondMode, [7], blocked.Token);
        await blockedAction.Should().ThrowAsync<OperationCanceledException>();

        await firstTransaction.RollbackAsync();
        await AcquireAsync(secondLock, secondMode, [7], CancellationToken.None);
        await secondTransaction.RollbackAsync();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SharedToExclusiveUpgrade_IsRejectedWithinSameTransaction()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ConfigurePrincipalsAsync(database);
        await using var context = database.CreateDbContext();
        await context.Database.OpenConnectionAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER = N'R1PrincipalA';");
        IAccountStatusMutationLock sut = new SqlServerAccountStatusMutationLock(
            new SqlServerTransactionApplicationLockExecutor(context));

        await sut.AcquireSharedAsync([8], CancellationToken.None);
        var action = () => sut.AcquireExclusiveAsync([8], CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be upgraded*");
        await transaction.RollbackAsync();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PrincipalOutsideApprovedRole_FailsClosed()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ConfigurePrincipalsAsync(database);
        await database.ExecuteNonQueryAsync("CREATE USER [R1NonMember] WITHOUT LOGIN;");
        await using var context = database.CreateDbContext();
        await context.Database.OpenConnectionAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER = N'R1NonMember';");
        IAccountStatusMutationLock sut = new SqlServerAccountStatusMutationLock(
            new SqlServerTransactionApplicationLockExecutor(context));

        var action = () => sut.AcquireSharedAsync([9], CancellationToken.None);

        var failure = await action.Should().ThrowAsync<TransactionLockAcquisitionException>();
        failure.Which.FailureKind.Should().Be(TransactionLockFailureKind.ProviderFailure);
        await transaction.RollbackAsync();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task OppositeInputOrder_BlocksOnLowestIdBeforeAcquiringHigherId()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ConfigurePrincipalsAsync(database);
        await using var blocker = await BeginAsPrincipalAsync(database, "R1PrincipalA");
        await using var subject = await BeginAsPrincipalAsync(database, "R1PrincipalB");
        await using var observer = await BeginAsPrincipalAsync(database, "R1PrincipalC");

        await Lock(blocker.Context).AcquireExclusiveAsync([1], CancellationToken.None);

        using var subjectCancellation = new CancellationTokenSource();
        var blockedAcquisition = Lock(subject.Context).AcquireExclusiveAsync(
            [2, 1],
            subjectCancellation.Token);
        Func<Task> blockedAction = async () => await blockedAcquisition;
        await WaitUntilBlockedByAsync(database, subject.Spid, blocker.Spid);

        using var observerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Lock(observer.Context).AcquireExclusiveAsync([2], observerTimeout.Token);
        subjectCancellation.Cancel();
        await blockedAction.Should().ThrowAsync<OperationCanceledException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task MultipleResources_ShareOneMonotonicTotalBudget()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ConfigurePrincipalsAsync(database);
        await using var firstBlocker = await BeginAsPrincipalAsync(database, "R1PrincipalA");
        await using var secondBlocker = await BeginAsPrincipalAsync(database, "R1PrincipalB");
        await using var subject = await BeginAsPrincipalAsync(database, "R1PrincipalC");

        await Lock(firstBlocker.Context).AcquireExclusiveAsync([1], CancellationToken.None);
        await Lock(secondBlocker.Context).AcquireExclusiveAsync([2], CancellationToken.None);

        var watch = Stopwatch.StartNew();
        var acquisition = Lock(subject.Context).AcquireExclusiveAsync([2, 1], CancellationToken.None);
        Func<Task> action = async () => await acquisition;
        await WaitUntilBlockedByAsync(database, subject.Spid, firstBlocker.Spid);
        await Task.Delay(TimeSpan.FromSeconds(6));
        await firstBlocker.Transaction.RollbackAsync();
        await WaitUntilBlockedByAsync(database, subject.Spid, secondBlocker.Spid);
        var failure = await action.Should().ThrowAsync<TransactionLockAcquisitionException>();
        watch.Stop();

        failure.Which.FailureKind.Should().Be(TransactionLockFailureKind.LockTimeout);
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(13));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(18),
            "the 15-second budget must not reset after the first resource");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task TransactionCommit_ReleasesLockToFreshPhysicalConnection()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ConfigurePrincipalsAsync(database);
        await using (var context = database.CreateDbContext())
        {
            await context.Database.OpenConnectionAsync();
            var originSpid = await ReadSpidAsync(context);
            await context.ExecuteInNonRetryingTransactionAsync(
                async token =>
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "EXECUTE AS USER = N'R1PrincipalA';",
                        token);
                    await Lock(context).AcquireExclusiveAsync([21], token);
                    return true;
                },
                CancellationToken.None);

            await AssertDifferentPhysicalConnectionCanAcquireAsync(database, 21, originSpid);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task TransactionException_ReleasesLockToFreshPhysicalConnection()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ConfigurePrincipalsAsync(database);
        await using (var context = database.CreateDbContext())
        {
            await context.Database.OpenConnectionAsync();
            var originSpid = await ReadSpidAsync(context);
            var action = () => context.ExecuteInNonRetryingTransactionAsync<bool>(
                async token =>
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "EXECUTE AS USER = N'R1PrincipalA';",
                        token);
                    await Lock(context).AcquireExclusiveAsync([22], token);
                    throw new InvalidOperationException("Synthetic operation failure.");
                },
                CancellationToken.None);

            await action.Should().ThrowAsync<InvalidOperationException>();
            await AssertDifferentPhysicalConnectionCanAcquireAsync(database, 22, originSpid);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task TransactionCancellation_ReleasesLockToFreshPhysicalConnection()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ConfigurePrincipalsAsync(database);
        await using (var context = database.CreateDbContext())
        using (var cancellation = new CancellationTokenSource())
        {
            await context.Database.OpenConnectionAsync();
            var originSpid = await ReadSpidAsync(context);
            var action = () => context.ExecuteInNonRetryingTransactionAsync<bool>(
                async token =>
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "EXECUTE AS USER = N'R1PrincipalA';",
                        token);
                    await Lock(context).AcquireExclusiveAsync([23], token);
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return true;
                },
                cancellation.Token);

            await action.Should().ThrowAsync<OperationCanceledException>();
            await AssertDifferentPhysicalConnectionCanAcquireAsync(database, 23, originSpid);
        }
    }

    private static Task AcquireAsync(
        IAccountStatusMutationLock sut,
        string mode,
        IReadOnlyCollection<long> accountIds,
        CancellationToken cancellationToken) =>
        mode switch
        {
            "Shared" => sut.AcquireSharedAsync(accountIds, cancellationToken),
            "Exclusive" => sut.AcquireExclusiveAsync(accountIds, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static IAccountStatusMutationLock Lock(ApplicationDbContext context) =>
        new SqlServerAccountStatusMutationLock(
            new SqlServerTransactionApplicationLockExecutor(context));

    private static async Task<PrincipalTransaction> BeginAsPrincipalAsync(
        SqlServerTestDatabase database,
        string principal)
    {
        var context = database.CreateDbContext();
        await context.Database.OpenConnectionAsync();
        var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlAsync($"EXECUTE AS USER = {principal};");
        return new PrincipalTransaction(context, transaction, await ReadSpidAsync(context));
    }

    private static async Task AssertDifferentPhysicalConnectionCanAcquireAsync(
        SqlServerTestDatabase database,
        long accountId,
        int originSpid)
    {
        await using var verifier = await BeginAsPrincipalAsync(database, "R1PrincipalB");
        verifier.Spid.Should().NotBe(originSpid);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Lock(verifier.Context).AcquireExclusiveAsync([accountId], timeout.Token);
    }

    private static async Task<int> ReadSpidAsync(ApplicationDbContext context)
    {
        var connection = (SqlConnection)context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction() as SqlTransaction;
        command.CommandText = "SELECT @@SPID;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntilBlockedByAsync(
        SqlServerTestDatabase database,
        int subjectSpid,
        int blockerSpid)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            var blocked = await database.ExecuteScalarAsync<int>($"""
                SELECT COUNT(*)
                FROM sys.dm_exec_requests
                WHERE session_id = {subjectSpid}
                  AND blocking_session_id = {blockerSpid}
                  AND wait_type LIKE N'LCK_M_%';
                """);
            if (blocked == 1)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Session {subjectSpid} was not observed blocked by {blockerSpid}.");
    }

    private static Task ConfigurePrincipalsAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            CREATE ROLE [TripMateAppLockRole] AUTHORIZATION [dbo];
            CREATE USER [R1PrincipalA] WITHOUT LOGIN;
            CREATE USER [R1PrincipalB] WITHOUT LOGIN;
            CREATE USER [R1PrincipalC] WITHOUT LOGIN;
            ALTER ROLE [TripMateAppLockRole] ADD MEMBER [R1PrincipalA];
            ALTER ROLE [TripMateAppLockRole] ADD MEMBER [R1PrincipalB];
            ALTER ROLE [TripMateAppLockRole] ADD MEMBER [R1PrincipalC];
            """);

    private sealed class PrincipalTransaction(
        ApplicationDbContext context,
        IDbContextTransaction transaction,
        int spid)
        : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;

        public IDbContextTransaction Transaction { get; } = transaction;

        public int Spid { get; } = spid;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Context.DisposeAsync();
        }
    }
}