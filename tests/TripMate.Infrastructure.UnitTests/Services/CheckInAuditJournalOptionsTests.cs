using FluentAssertions;

using Microsoft.Extensions.Configuration;

using TripMate.Infrastructure.Services.Audit;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class CheckInAuditJournalOptionsTests
{
    [Fact]
    public void Validate_DisabledDefaults_Succeeds()
    {
        var result = new CheckInAuditJournalOptionsValidator().Validate(
            null,
            new CheckInAuditJournalOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_EnabledWithDedicatedConnectionAndDistinctStreams_Succeeds()
    {
        IConfiguration configuration = Configuration("localhost:6379");
        var options = ValidEnabled();

        var result = new CheckInAuditJournalOptionsValidator(configuration)
            .Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_EnabledWithoutConnection_Fails()
    {
        var result = new CheckInAuditJournalOptionsValidator(Configuration(null))
            .Validate(null, ValidEnabled());

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ConnectionStrings:Redis");
    }

    [Fact]
    public void Validate_AdmissionAndOutcomeUsingSameStream_Fails()
    {
        var options = ValidEnabled();
        options.RejectionStreamKey = options.AdmissionStreamKey;

        var result = new CheckInAuditJournalOptionsValidator(Configuration("localhost:6379"))
            .Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.RejectionStreamKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validate_InvalidMaxAppendAttempts_Fails(int attempts)
    {
        var options = ValidEnabled();
        options.MaxAppendAttempts = attempts;

        var result = new CheckInAuditJournalOptionsValidator(Configuration("localhost:6379"))
            .Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.MaxAppendAttempts));
    }

    [Fact]
    public void Validate_DurabilityTimeoutLongerThanCommandTimeout_Fails()
    {
        var options = ValidEnabled();
        options.CommandTimeout = TimeSpan.FromSeconds(1);
        options.DurabilityTimeout = TimeSpan.FromSeconds(2);

        var result = new CheckInAuditJournalOptionsValidator(Configuration("localhost:6379"))
            .Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.DurabilityTimeout));
    }

    private static CheckInAuditJournalOptions ValidEnabled() => new()
    {
        Enabled = true,
        RedisConnectionStringName = "Redis",
        AdmissionStreamKey = "tripmate:checkin:audit:admissions",
        RejectionStreamKey = "tripmate:checkin:audit:rejections",
        ReceiptKeyPrefix = "tripmate:checkin:audit:receipts",
        CommandTimeout = TimeSpan.FromSeconds(2),
        DurabilityTimeout = TimeSpan.FromSeconds(1),
        MaxAppendAttempts = 2,
    };

    private static IConfiguration Configuration(string? connection) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = connection,
            })
            .Build();
}