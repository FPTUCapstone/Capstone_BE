using System.Diagnostics;

using FluentAssertions;

using StackExchange.Redis;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Operator.CheckIn.Audit;
using TripMate.Infrastructure.Services.Audit;

using Xunit.Abstractions;

namespace TripMate.Api.IntegrationTests.Tickets;

/// <summary>
/// Two-invocation restart probe for an isolated Redis owned by the test operator.
/// Run once with phase=write, restart that Redis through the approved harness, then
/// run with phase=verify using the same run id. The test never restarts a service.
/// </summary>
public sealed class CheckInAuditAofRestartTests(ITestOutputHelper output)
{
    private const string ConnectionVariable = "TRIPMATE_REDIS_AOF_TEST_CONNECTION";
    private const string PhaseVariable = "TRIPMATE_REDIS_AOF_PROBE_PHASE";
    private const string RunIdVariable = "TRIPMATE_REDIS_AOF_PROBE_RUN_ID";

    [AofRedisFact]
    public async Task ConfirmedEvent_SurvivesOperatorControlledRestart_AndReplayIsIdempotent()
    {
        string connectionString = Required(ConnectionVariable);
        string phase = Required(PhaseVariable).ToLowerInvariant();
        string runId = Required(RunIdVariable);
        runId.Should().MatchRegex("^[a-zA-Z0-9_-]{1,64}$");

        ConfigurationOptions configuration = RedisTestFixture.BuildConfiguration(connectionString);
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
        await AssertAofAlwaysAsync(multiplexer);

        string prefix = $"tripmate:test:tm89:aof-restart:{runId}";
        var options = Options(prefix);
        await using var connection = new CheckInAuditRedisConnection(multiplexer, ownsMultiplexer: false);
        var journal = new RedisCheckInAuditJournal(connection, options);
        CheckInAuditEvent auditEvent = ProbeEvent(runId);

        if (phase == "write")
        {
            (await journal.AppendAsync(auditEvent, CancellationToken.None))
                .Should().Be(CheckInAuditWriteResult.Confirmed,
                    "WAITAOF must acknowledge a local fsync before the write phase succeeds");
            (await multiplexer.GetDatabase().StreamLengthAsync(options.AdmissionStreamKey))
                .Should().Be(1);
            output.WriteLine("WRITE_CONFIRMED runId={0}; restart the isolated Redis before phase=verify.", runId);
            return;
        }

        phase.Should().Be("verify", "the supported phases are write and verify");
        (await multiplexer.GetDatabase().StreamLengthAsync(options.AdmissionStreamKey))
            .Should().Be(1, "the previously fsynced stream event must survive restart");
        (await journal.AppendAsync(auditEvent, CancellationToken.None))
            .Should().Be(CheckInAuditWriteResult.Confirmed);
        (await multiplexer.GetDatabase().StreamLengthAsync(options.AdmissionStreamKey))
            .Should().Be(1, "replay of the same EventId and payload must not duplicate the event");
    }

    [AofRedisFact]
    public async Task AppendAndWaitAof_ReportsLatencyDistributionWithoutInventingAnSla()
    {
        string connectionString = Required(ConnectionVariable);
        string runId = Required(RunIdVariable);
        ConfigurationOptions configuration = RedisTestFixture.BuildConfiguration(connectionString);
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
        await AssertAofAlwaysAsync(multiplexer);
        string prefix = $"tripmate:test:tm89:aof-benchmark:{runId}:{Guid.NewGuid():N}";
        var options = Options(prefix);
        await using var connection = new CheckInAuditRedisConnection(multiplexer, ownsMultiplexer: false);
        var journal = new RedisCheckInAuditJournal(connection, options);
        var samples = new List<double>();

        for (int index = 0; index < 50; index++)
        {
            var auditEvent = CheckInAuditEvent.Admission(
                Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, 41, 51);
            var stopwatch = Stopwatch.StartNew();
            (await journal.AppendAsync(auditEvent, CancellationToken.None))
                .Should().Be(CheckInAuditWriteResult.Confirmed);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        output.WriteLine(
            "append+WAITAOF milliseconds: p50={0:F2}, p95={1:F2}, p99={2:F2}; samples={3}",
            Percentile(samples, 0.50), Percentile(samples, 0.95),
            Percentile(samples, 0.99), samples.Count);
    }

    private static async Task AssertAofAlwaysAsync(IConnectionMultiplexer multiplexer)
    {
        IServer server = multiplexer.GetServers().Single();
        Dictionary<string, string> values = (await server.ConfigGetAsync("append*")).ToDictionary();
        values["appendonly"].Should().Be("yes");
        values["appendfsync"].Should().Be("always");
        Dictionary<string, string> directory = (await server.ConfigGetAsync("dir")).ToDictionary();
        directory["dir"].Replace('\\', '/').Should().EndWith("/data");
    }

    private static CheckInAuditEvent ProbeEvent(string runId)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(runId));
        var eventBytes = hash[..16].ToArray();
        var attemptBytes = hash[16..32].ToArray();
        return CheckInAuditEvent.Admission(
            new Guid(eventBytes), new Guid(attemptBytes),
            DateTimeOffset.Parse("2026-11-05T01:02:03Z"), 41, 51);
    }

    private static CheckInAuditJournalOptions Options(string prefix) => new()
    {
        Enabled = true,
        AdmissionStreamKey = $"{prefix}:admissions",
        RejectionStreamKey = $"{prefix}:rejections",
        ReceiptKeyPrefix = $"{prefix}:receipts",
        CommandTimeout = TimeSpan.FromSeconds(3),
        DurabilityTimeout = TimeSpan.FromSeconds(2),
        MaxAppendAttempts = 2,
    };

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} for the isolated AOF probe.");

    private static double Percentile(IReadOnlyList<double> sorted, double percentile) =>
        sorted[(int)Math.Ceiling(percentile * sorted.Count) - 1];
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class AofRedisFactAttribute : FactAttribute
{
    public AofRedisFactAttribute()
    {
        string[] required =
        [
            "TRIPMATE_REDIS_AOF_TEST_CONNECTION",
            "TRIPMATE_REDIS_AOF_PROBE_PHASE",
            "TRIPMATE_REDIS_AOF_PROBE_RUN_ID",
        ];
        string[] missing = required
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        if (missing.Length > 0)
        {
            Skip = $"Set {string.Join(", ", missing)} to run the isolated AOF probe.";
        }
    }
}