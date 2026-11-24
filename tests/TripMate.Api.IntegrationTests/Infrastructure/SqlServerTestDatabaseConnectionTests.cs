using FluentAssertions;

using Microsoft.Data.SqlClient;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class SqlServerTestDatabaseConnectionTests
{
    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteScriptAsync_OpenTimeout_RecoversBeforeExecutingTheScript(int failures)
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var path = Path.Combine(Path.GetTempPath(), $"tripmate-connection-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, "CREATE TABLE dbo.ConnectionRetryProbe (value INT NOT NULL);\nGO\nINSERT dbo.ConnectionRetryProbe VALUES (1);");
        try
        {
            using var probe = new ConnectionProbe(database.DatabaseName, openFailures: failures);
            var failure = await Record.ExceptionAsync(() => database.ExecuteScriptAsync(path));

            if (failure is not null) failure.Should().BeOfType<TimeoutException>();
            failure.Should().BeNull("a transient failure before opening the SQL connection must be recoverable");
            probe.OpenAttempts.Should().BeInRange(failures + 1, 3);
            probe.DistinctConnections.Should().Be(1);
            probe.Dispose();
            (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ConnectionRetryProbe;"))
                .Should().Be(1, "the script is executed only after the connection is open");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExecuteScriptAsync_PersistentOpenTimeout_StopsAfterThreeAttempts()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var path = Path.Combine(Path.GetTempPath(), $"tripmate-connection-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, "CREATE TABLE dbo.ConnectionRetryProbe (value INT NOT NULL);");
        try
        {
            using var probe = new ConnectionProbe(database.DatabaseName, openFailures: int.MaxValue);
            var failure = await Record.ExceptionAsync(() => database.ExecuteScriptAsync(path));

            failure.Should().BeOfType<TimeoutException>();
            probe.OpenAttempts.Should().Be(3);
            probe.Dispose();
            (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'ConnectionRetryProbe';"))
                .Should().Be(0, "no batch may execute when opening never succeeds");
        }
        finally { File.Delete(path); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExecuteScriptAsync_ExecutionFailure_DoesNotReplayCommittedBatches()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var path = Path.Combine(Path.GetTempPath(), $"tripmate-connection-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, """
            CREATE TABLE dbo.ConnectionRetryProbe (value INT NOT NULL);
            INSERT dbo.ConnectionRetryProbe VALUES (1);
            GO
            THROW 51001, 'CI787: failure after a previous batch committed.', 1;
            """);
        try
        {
            using var probe = new ConnectionProbe(database.DatabaseName);
            var failure = await Record.ExceptionAsync(() => database.ExecuteScriptAsync(path));

            failure.Should().BeOfType<SqlException>().Which.Number.Should().Be(51001);
            probe.DistinctConnections.Should().Be(1);
            probe.Dispose();
            (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ConnectionRetryProbe;"))
                .Should().Be(1, "the first committed batch must not be executed again");
        }
        finally { File.Delete(path); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExecuteScalarAsync_NonTimeoutOpenFailure_PropagatesWithoutRetry()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var error = new InvalidOperationException("CI787: non-transient opening failure.");
        using var probe = new ConnectionProbe(database.DatabaseName, openFailures: int.MaxValue, openError: error);

        var failure = await Record.ExceptionAsync(() => database.ExecuteScalarAsync<int>("SELECT 1;"));

        failure.Should().BeSameAs(error);
        probe.OpenAttempts.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExecuteScalarAsync_CancelledAfterOpenTimeout_DoesNotRetry()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        using var cancellation = new CancellationTokenSource();
        using var probe = new ConnectionProbe(database.DatabaseName, openFailures: 1, beforeOpenFailure: cancellation.Cancel);

        var failure = await Record.ExceptionAsync(() => database.ExecuteScalarAsync<int>("SELECT 1;", cancellation.Token));

        failure.Should().BeAssignableTo<OperationCanceledException>();
        probe.OpenAttempts.Should().Be(1);
    }
}